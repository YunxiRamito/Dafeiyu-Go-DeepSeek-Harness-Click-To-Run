# Startup regression fixtures

`session-pwsh.original.js` is the unmodified `lib/session-pwsh.js` from the
official npm `dsh-ops@0.3.0` package, published by T-Auto and the dsh-ops
contributors. The plugin code is MIT licensed; the package license and notice
are retained beside this fixture. The upstream repository is
`https://github.com/T-Auto/dsh-ops`.

The downloaded package integrity was checked against its npm metadata:

`sha512-DZhLjvxVuPrdhDzd6ySVK1reg4uZ0HE/ep3t+r8/3eMH/ZSbNwuhlAcr1M9lfEuwbf+Qq4u4wl8EkEbdsTk8yw==`

The original module SHA-256 is
`53E251480AE27CE16BB84099F07CE670633C7BAB49C155E5A0024A7991B4F8DE`.

`feedback5.sanitized.txt` retains the relevant error sequence from launcher
feedback #5. Paths have been replaced with synthetic fixture paths. It contains
no feedback identity, user directory, profile data or credentials. The test can
also accept the locally downloaded full log as its optional fourth argument.
