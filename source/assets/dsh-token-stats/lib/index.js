// dsh-token-stats —— Host 半身（node 侧）
//
// 只做一件事：挂 `llm/stream` 这条 waterfall，把每次模型调用的 usage 追加写进
//   <DSH_HOME>/storages/token-stats/usage.jsonl
// 启动器主页那张「Token 用量与余额」卡片读的就是这份文件。
//
// 为什么自己写而不是装社区那个 @zerro223/dsh-token-usage：
// 它把 `@deepseek-ai/dsh-home-paths` 写进了 peerDependencies（^0.1.0-rc.6），
// 而 DSH 从 0.1.x 走到 0.2.x 之后这个包跟着涨到了 0.2.0-rc.1，
// DSH 的插件兼容性校验（dsh-app-boot：只看插件 package.json 里的 peerDependencies）
// 于是直接把它拦下：装得上、一跑就被判「与当前 DSH 不兼容」。
//
// 所以这里刻意做到「零依赖」：
//   · 不 import 任何 @deepseek-ai/* 的包（DSH 的 home 目录自己解析：DSH_HOME → ~/.dsh）
//   · package.json 里一行 peerDependencies 都不写（没有这条，校验就放行）
//   · 不做界面、不挂 HTTP 路由、不联网（不装 webServer，也就不挑 profile）
//
// 记录字段与启动器 TokenUsageService 的读法一一对应：
//   ts / provider / model / sessionId / inputTokens / outputTokens /
//   cacheReadTokens / cacheWriteTokens / reasoningTokens

import { appendFile, mkdir } from 'node:fs/promises';
import { homedir } from 'node:os';
import { join } from 'node:path';

/** cordis 插件的稳定名字。 */
export const name = 'dsh-token-stats';

/** 攒够这么多行就立刻落盘。 */
const FLUSH_BATCH = 64;

/** 否则最多等这么久（毫秒）。 */
const FLUSH_MS = 1000;

/** 写失败后的重试间隔与次数上限：宁可慢一点，也别把统计悄悄丢了。 */
const FLUSH_RETRY_MS = 2000;
const FLUSH_MAX_ATTEMPTS = 8;

/** DSH 的 home：环境变量优先（启动器就是用 DSH_HOME 把服务指到 <dshRoot>\.dsh 的）。 */
function resolveHome() {
  const fromEnv = process.env.DSH_HOME;
  if (typeof fromEnv === 'string' && fromEnv.trim() !== '') {
    return fromEnv.trim();
  }
  return join(homedir(), '.dsh');
}

/** 取一个非负整数（上游偶尔给字符串或 undefined）。 */
function toCount(value) {
  const n = Number(value);
  return Number.isFinite(n) && n > 0 ? Math.round(n) : 0;
}

/**
 * 插件体。
 * @param ctx - cordis 上下文（只需要 ctx.on / ctx.effect / ctx.logger）。
 */
export function apply(ctx) {
  const dir = join(resolveHome(), 'storages', 'token-stats');
  const file = join(dir, 'usage.jsonl');

  let pending = [];
  let timer = null;
  let chain = Promise.resolve();
  let attempts = 0;
  let warned = false;

  function warn(message, error) {
    const text = error === undefined ? message : message + ' ' + (error && error.message ? error.message : String(error));
    if (ctx.logger && typeof ctx.logger.warn === 'function') {
      ctx.logger.warn(text);
    } else {
      console.warn(text);
    }
  }

  function flush() {
    if (timer !== null) {
      clearTimeout(timer);
      timer = null;
    }
    if (pending.length === 0) return;
    const batch = pending.join('');
    pending = [];
    chain = chain
      .then(() => mkdir(dir, { recursive: true }))
      .then(() => appendFile(file, batch, 'utf8'))
      .then(() => {
        attempts = 0;
        warned = false;
      })
      .catch((error) => {
        if (!warned) {
          warned = true;
          warn('[dsh-token-stats] 写不进用量文件', error);
        }
        if (attempts < FLUSH_MAX_ATTEMPTS) {
          attempts += 1;
          pending = [batch, ...pending];
          if (timer === null) {
            timer = setTimeout(flush, Math.min(FLUSH_RETRY_MS * attempts, 30000));
          }
        } else {
          warn('[dsh-token-stats] 放弃这一批（重试 ' + FLUSH_MAX_ATTEMPTS + ' 次都没成功）');
        }
      });
  }

  function persist(record) {
    pending.push(JSON.stringify(record) + '\n');
    if (pending.length >= FLUSH_BATCH) {
      flush();
    } else if (timer === null) {
      timer = setTimeout(flush, FLUSH_MS);
    }
  }

  // 每条真正打到 provider 的调用都会经过这条 waterfall 一次（重试的每次尝试
  // 也是新的一次 stream 调用），所以这里记下的条数 = 实际请求数。
  ctx.on('llm/stream', async function* (options, next) {
    const inner = next();
    let usage = null;
    for await (const chunk of inner) {
      if (chunk !== null && typeof chunk === 'object' && chunk.type === 'usage' && chunk.usage) {
        usage = chunk.usage;
      }
      yield chunk;
    }

    if (usage === null) {
      return;
    }

    try {
      const record = {
        ts: Date.now(),
        provider: options && options.provider !== undefined ? options.provider : null,
        model: options && options.model !== undefined ? options.model : null,
        sessionId: options && options.sessionId !== undefined ? options.sessionId : null,
        inputTokens: toCount(usage.inputTokens),
        outputTokens: toCount(usage.outputTokens),
        cacheReadTokens: toCount(usage.cacheReadTokens),
        cacheWriteTokens: toCount(usage.cacheWriteTokens),
        reasoningTokens: toCount(usage.reasoningTokens)
      };
      persist(record);
    } catch (error) {
      warn('[dsh-token-stats] 记一条用量失败', error);
    }
  });

  // 卸载 / 热重载时把没落盘的那点尾巴写掉
  ctx.effect(() => {
    return () => {
      flush();
    };
  }, 'dsh-token-stats: flush on dispose');
}
