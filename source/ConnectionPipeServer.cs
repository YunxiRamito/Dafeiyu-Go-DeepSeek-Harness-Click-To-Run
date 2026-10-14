using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeepSeekHarnessLauncher.Backup;
using Microsoft.Win32.SafeHandles;

namespace DeepSeekHarnessLauncher
{
    internal sealed class ConnectionPipeServer : IDisposable
    {
        internal const string Protocol = "dafeiyu-go.connection.v1";
        private const int MaximumRequestBytes = 1024 * 1024;
        private readonly Func<string, JsonElement, CancellationToken, Task<object>> dispatch;
        private readonly CancellationTokenSource stop = new();
        private readonly string descriptorPath;
        private readonly Action<string> log;
        private readonly ConnectionPipeReservation.Owner ownership;
        private readonly bool ownsReservation;
        private readonly SemaphoreSlim cleanup = new(1, 1);
        private readonly object stateSync = new();
        private readonly string descriptorRevision = Guid.NewGuid().ToString("N");
        private readonly byte[] descriptorBytes;
        private readonly List<NamedPipeServerStream> instances = new();
        private readonly Dictionary<NamedPipeServerStream, SafePipeHandle> instanceHandles = new();
        private readonly List<Task> listeners = new();
        private Task starting;
        private Task cancelling;
        private NamedPipeServerStream anchor;
        private bool tasksJoined;
        private bool listenerReturned;
        private int state; // 0 new, 1 starting, 2 ready, 3 stopping, 4 closed
        internal ConnectionPipeReservation Reservation { get; }
        internal string PipeName => Reservation.PipeName;
        internal bool IsReady
        {
            get { lock (stateSync) return state == 2 && !stop.IsCancellationRequested && listeners.All(task => !task.IsCompleted); }
        }

        internal ConnectionPipeServer(string dshHome,
            Func<string, JsonElement, CancellationToken, Task<object>> dispatch, Action<string> log,
            ConnectionPipeReservation reservation = null)
        {
            this.dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
            this.log = log;
            ownsReservation = reservation == null;
            Reservation = reservation ?? new ConnectionPipeReservation(dshHome,
                "DafeiyuGo.Connection." + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
            if (!PcOfficialBootstrapConfiguration.SamePath(Reservation.DshHome, dshHome))
                throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.HostUnavailable);
            ownership = Reservation.Hold();
            descriptorPath = Path.Combine(Reservation.DshHome, "dafeiyu-connection.json");
            descriptorBytes = JsonSerializer.SerializeToUtf8Bytes(new { protocol = Protocol, pipe_name = PipeName,
                pid = Environment.ProcessId, descriptor_revision = descriptorRevision });
        }

        internal void Start() => Task.Run(StartAsync).GetAwaiter().GetResult();
        internal Task StartAsync()
        {
            lock (stateSync)
            {
                if (state != 0) throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.HostUnavailable);
                state = 1;
                // The real ownership lease is already held before scheduling.
                // Task.Run avoids blocking a captured UI context in Start().
                starting = Task.Run(StartCoreAsync);
                ownership.Track(starting);
                return CompleteStartupAsync(starting);
            }
        }
        private async Task CompleteStartupAsync(Task actualStartup)
        {
            try { await actualStartup.ConfigureAwait(false); }
            catch
            {
                if (!await Task.Run(() => StopAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false))
                    throw new PcOfficialCleanupIncompleteException();
                throw;
            }
        }
        private async Task StartCoreAsync()
        {
            try
            {
                stop.Token.ThrowIfCancellationRequested();
                anchor = ownership.ClaimListener();
                instances.Add(anchor);
                instanceHandles.Add(anchor, anchor.SafePipeHandle);
                for (int index = 1; index < 8; index++)
                {
                    var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 16,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    instances.Add(pipe);
                    instanceHandles.Add(pipe, pipe.SafePipeHandle);
                }
                var ready = new List<Task>();
                foreach (var pipe in instances)
                {
                    var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Task listener = ListenSlotAsync(pipe, waiting);
                    ownership.Track(listener); listeners.Add(listener); ready.Add(waiting.Task);
                }
                await Task.WhenAll(ready).ConfigureAwait(false);
                lock (stateSync)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (state != 1 || listeners.Any(task => task.IsCompleted)) throw new IOException("pipe_listener_unavailable");
                    PublishDescriptor();
                    state = 2;
                }
            }
            catch
            {
                lock (stateSync) { state = 3; BeginCancellation(); }
                // A listener can fail after another task has published the
                // descriptor. Withdraw only this server's exact revision; if
                // the guarded removal cannot complete, ownership stays live
                // and StopAsync can retry it.
                TryRemoveOwnDescriptor();
                throw;
            }
        }
        private async Task ListenSlotAsync(NamedPipeServerStream pipe, TaskCompletionSource ready)
        {
            bool accepted = false;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    Task wait = pipe.WaitForConnectionAsync(stop.Token);
                    ownership.Track(wait);
                    ready.TrySetResult();
                    try { await wait.ConfigureAwait(false); }
                    finally { ownership.ForgetCompleted(wait); }
                    accepted = true;
                    Task serve = ServeAsync(pipe);
                    ownership.Track(serve);
                    try { await serve.ConfigureAwait(false); }
                    finally { ownership.ForgetCompleted(serve); }
                    pipe.Disconnect();
                    accepted = false;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error)
            {
                ready.TrySetException(error);
                SafeLog("连接插件本地通道暂不可用。");
                lock (stateSync) { state = 3; BeginCancellation(); }
                TryRemoveOwnDescriptor();
            }
            finally
            {
                ready.TrySetCanceled();
                if (accepted || pipe.IsConnected) pipe.Disconnect();
                // No listener disposes the original first-instance handle.
                // Stop joins every loop and child before closing other handles.
            }
        }

        private async Task ServeAsync(NamedPipeServerStream pipe)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            string requestId = null;
            byte[] requestBytes = null;
            Task peerClosed = null;
            try
            {
                requestBytes = await ReadRequestAsync(pipe, deadline.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(requestBytes, new JsonDocumentOptions { MaxDepth = 32 });
                JsonElement envelope = document.RootElement;
                if (envelope.ValueKind != JsonValueKind.Object
                    || !envelope.TryGetProperty("request_id", out JsonElement id)
                    || id.ValueKind != JsonValueKind.String || String.IsNullOrWhiteSpace(id.GetString())
                    || id.GetString().Length > 128)
                    throw new AriesServiceException("protocol_error", "请求编号无效。");
                requestId = id.GetString();
                if (!envelope.TryGetProperty("protocol", out JsonElement protocol)
                    || protocol.ValueKind != JsonValueKind.String || protocol.GetString() != Protocol
                    || !envelope.TryGetProperty("method", out JsonElement method)
                    || method.ValueKind != JsonValueKind.String || method.GetString().Length > 80
                    || !envelope.TryGetProperty("params", out JsonElement parameters)
                    || parameters.ValueKind != JsonValueKind.Object)
                    throw new AriesServiceException("protocol_error", "本地连接协议不兼容。");
                peerClosed = WatchPeerAsync(pipe, deadline);
                ownership.Track(peerClosed);
                Task<object> actualDispatch = dispatch(method.GetString(), parameters, deadline.Token)
                    ?? throw new InvalidOperationException("missing_dispatch_task");
                ownership.Track(actualDispatch);
                object data;
                try { data = await actualDispatch.ConfigureAwait(false); }
                finally { ownership.ForgetCompleted(actualDispatch); }
                deadline.Token.ThrowIfCancellationRequested();
                await WriteResponseAsync(pipe, new { success = true, request_id = requestId, data }, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                string code = exception is AriesProviderException provider ? provider.Code
                    : exception is AriesServiceException api ? api.Code
                    : exception is OperationCanceledException ? stop.IsCancellationRequested ? "request_cancelled" : "request_timeout"
                    : exception is JsonException || exception is InvalidDataException ? "protocol_error"
                    : "connection_unavailable";
                string message = exception is AriesServiceException or AriesProviderException ? exception.Message : "本地连接请求未完成。";
                if (!stop.IsCancellationRequested)
                    try
                    {
                        using var responseDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await WriteResponseAsync(pipe, new
                        {
                            success = false, request_id = requestId, error_code = code, message,
                            retryable = exception is AriesProviderException failure ? failure.Retryable
                                : code is "connection_unavailable" or "request_timeout" or "transport_unavailable"
                        }, responseDeadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception) { }
            }
            finally
            {
                await deadline.CancelAsync().ConfigureAwait(false);
                if (peerClosed != null)
                {
                    try { await peerClosed.ConfigureAwait(false); }
                    finally { ownership.ForgetCompleted(peerClosed); }
                }
                if (requestBytes != null) CryptographicOperations.ZeroMemory(requestBytes);
            }
        }
        private static async Task WatchPeerAsync(Stream pipe, CancellationTokenSource operation)
        {
            try { await pipe.ReadAsync(new byte[1], operation.Token).ConfigureAwait(false); await operation.CancelAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException) { await operation.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }
        private static async Task<byte[]> ReadRequestAsync(Stream pipe, CancellationToken token)
        {
            using var buffer = new MemoryStream();
            byte[] block = new byte[4096];
            try
            {
                while (true)
                {
                    int length = await pipe.ReadAsync(block, token).ConfigureAwait(false);
                    if (length == 0) throw new IOException("Peer closed.");
                    int newline = Array.IndexOf(block, (byte)10, 0, length);
                    int count = newline < 0 ? length : newline;
                    if (buffer.Length + count > MaximumRequestBytes) throw new InvalidDataException("Request too large.");
                    buffer.Write(block, 0, count);
                    if (newline >= 0)
                    {
                        if (newline + 1 != length) throw new InvalidDataException("Only one request is allowed.");
                        return buffer.ToArray();
                    }
                }
            }
            finally { CryptographicOperations.ZeroMemory(block); }
        }
        private static async Task WriteResponseAsync(Stream pipe, object response, CancellationToken token)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(response);
            try
            {
                if (bytes.Length > 2 * MaximumRequestBytes) throw new InvalidDataException("Response too large.");
                await pipe.WriteAsync(bytes, token).ConfigureAwait(false);
                await pipe.WriteAsync(new byte[] { 10 }, token).ConfigureAwait(false);
                await pipe.FlushAsync(token).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        private void PublishDescriptor()
        {
            using var descriptorLock = DymOfficialRecoveryFiles.Lock(descriptorPath, "connection-descriptor");
            byte[] before = DymOfficialRecoveryFiles.Read(descriptorPath, true, 64 * 1024);
            if (before != null && !IsOwnDescriptor(before)) throw new IOException("connection_descriptor_owned_elsewhere");
            DymOfficialRecoveryFiles.Publish(descriptorPath, descriptorBytes, before != null,
                DymOfficialRecoveryFiles.Revision(before), 64 * 1024);
        }
        private bool IsOwnDescriptor(byte[] bytes)
        {
            if (!bytes.AsSpan().SequenceEqual(descriptorBytes)) return false;
            try
            {
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                return root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("protocol", out var protocol) && protocol.ValueKind == JsonValueKind.String && protocol.GetString() == Protocol
                    && root.TryGetProperty("pipe_name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == PipeName
                    && root.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out int value) && value == Environment.ProcessId
                    && root.TryGetProperty("descriptor_revision", out var revision)
                    && revision.ValueKind == JsonValueKind.String && revision.GetString() == descriptorRevision;
            }
            catch (JsonException) { return false; }
        }
        private bool TryRemoveOwnDescriptor()
        {
            try
            {
                using var descriptorLock = DymOfficialRecoveryFiles.Lock(descriptorPath, "connection-descriptor");
                DymOfficialRecoveryFiles.NoLinks(descriptorPath);
                // Validate and remove through one exclusive Windows file handle.
                // A read-then-path-delete would let a non-cooperating replacement
                // swap in somebody else's revision between the two operations.
                using var handle = CreateFile(descriptorPath, 0x80000000 | 0x00010000, 0, IntPtr.Zero,
                    3, 0x00200000 | 0x08000000, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error is 2 or 3) return true;
                    throw new Win32Exception(error);
                }
                if (!GetFileInformationByHandle(handle, out var info))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if ((info.Attributes & (0x10 | 0x400)) != 0 || info.NumberOfLinks != 1)
                    throw new IOException("connection_descriptor_not_regular");
                using var input = new FileStream(handle, FileAccess.Read);
                if (input.Length != descriptorBytes.Length) return true;
                byte[] before = new byte[descriptorBytes.Length];
                input.ReadExactly(before);
                if (input.ReadByte() >= 0 || !IsOwnDescriptor(before)) return true;
                var remove = new FileDispositionInfo { DeleteFile = 1 };
                if (!SetFileInformationByHandle(handle, 4, ref remove, 1))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return true;
            }
            catch (Exception)
            {
                SafeLog("连接插件本地通道发现状态撤回等待重试。");
                return false;
            }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInfo { internal byte DeleteFile; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            internal uint VolumeSerialNumber, SizeHigh, SizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string filename, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
            ref FileDispositionInfo information, uint bufferSize);
        private void BeginCancellation()
        {
            if (cancelling != null) return;
            cancelling = stop.CancelAsync();
            ownership.Track(cancelling);
        }
        private void SafeLog(string message) { try { log?.Invoke(message); } catch (Exception) { } }

        internal async Task<bool> StopAsync(TimeSpan timeout)
        {
            if (!await cleanup.WaitAsync(timeout).ConfigureAwait(false)) return false;
            try
            {
                lock (stateSync)
                {
                    if (state == 4) return true;
                    state = 3; BeginCancellation();
                }
                // Stop advertising immediately. Pending operations retain their
                // original handle/owner, without claiming new requests are ready.
                if (!TryRemoveOwnDescriptor()) return false;
                if (!tasksJoined)
                {
                    // Start must truly finish before snapshotting listener Tasks.
                    try { if (starting != null) await starting.WaitAsync(timeout).ConfigureAwait(false); }
                    catch (TimeoutException) { return false; }
                    catch (Exception) when (starting?.IsCompleted == true) { }
                    Task[] pending;
                    lock (stateSync) pending = listeners.Append(cancelling).ToArray();
                    try { await Task.WhenAll(pending).WaitAsync(timeout).ConfigureAwait(false); }
                    catch (TimeoutException) { return false; }
                    catch (Exception) when (pending.All(task => task.IsCompleted)) { }
                    foreach (Task task in pending) ownership.ForgetCompleted(task);
                    if (starting != null) ownership.ForgetCompleted(starting);
                    tasksJoined = true;
                }
                foreach (var pipe in instances.ToArray())
                    if (!ReferenceEquals(pipe, anchor))
                    {
                        // The loop can end because its handle was closed. Keep
                        // the actual originally created handle: querying the
                        // stream after that failure throws and prevents retry.
                        if (!instanceHandles.TryGetValue(pipe, out var handle))
                            throw new PcOfficialCleanupIncompleteException();
                        pipe.Dispose();
                        if (!handle.IsClosed) throw new PcOfficialCleanupIncompleteException();
                        instances.Remove(pipe);
                        instanceHandles.Remove(pipe);
                    }
                // Do not hand the original listener to another owner while a
                // late own descriptor still needs its guarded withdrawal.
                if (!TryRemoveOwnDescriptor()) return false;
                if (anchor != null && !listenerReturned) { ownership.ReturnListener(); listenerReturned = true; }
                ownership.Dispose();
                if (ownsReservation) Reservation.Dispose();
                lock (stateSync) { state = 4; listeners.Clear(); instances.Clear(); instanceHandles.Clear(); }
                stop.Dispose();
                return true;
            }
            finally { cleanup.Release(); }
        }
        public void Dispose()
        {
            if (!Task.Run(() => StopAsync(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult())
                throw new PcOfficialCleanupIncompleteException();
        }
    }
}
