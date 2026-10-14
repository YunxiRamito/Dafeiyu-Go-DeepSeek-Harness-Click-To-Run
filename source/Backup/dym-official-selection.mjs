// Dafeiyu-GO (MIT): pure portable v1 official selection. No storage receipt.
const plain = value => value && typeof value === 'object' && !Array.isArray(value);
const check = value => { if (!value) throw new Error('DYM_OFFICIAL_INVALID'); };
export const validReference = value => typeof value === 'string' && /^[A-Za-z_][A-Za-z0-9_]*$/.test(value);
export const validSecret = value => typeof value === 'string' && value.length > 0 && value.length <= 65536 && !/[\0\r\n]/.test(value);
const unknown = () => ({ known: false, reference: null, key: null });

export function credentialSelection(document) {
  check(plain(document) && document.version === 1 && plain(document.refs) && plain(document.records));
  check(Object.keys(document).every(name => ['version', 'refs', 'records', 'portableFiles', 'officialRef', 'launcherDeepSeekKey'].includes(name)));
  check(Object.keys(document.refs).length <= 128 && Object.keys(document.records).length <= 127);
  check(document.portableFiles === undefined || plain(document.portableFiles));
  for (const [name, key] of Object.entries(document.refs)) check(validReference(name) && validSecret(key));
  if (Object.hasOwn(document, 'launcherDeepSeekKey')) check(validSecret(document.launcherDeepSeekKey));
  if (Object.hasOwn(document, 'officialRef')) {
    check(validReference(document.officialRef));
    return { known: true, reference: document.officialRef,
      key: document.launcherDeepSeekKey ?? document.refs[document.officialRef] ?? null };
  }
  const key = document.launcherDeepSeekKey ?? document.refs.DEEPSEEK_API_KEY ?? null;
  return key === null ? unknown() : { known: true, reference: 'DEEPSEEK_API_KEY', key };
}

export function officialSelection(native, credentials) {
  if (credentials) {
    const selected = credentialSelection(credentials);
    if (selected.known) return selected;
  }
  const official = native?.credentials?.deepseek;
  if (!official) return unknown();
  check(plain(official) && official.type === 'api_key');
  if (official.key === undefined) return unknown();
  check(validSecret(official.key));
  return { known: true, reference: 'DEEPSEEK_API_KEY', key: official.key };
}

export function isOfficialReference(reference, ...selectors) {
  return reference === 'DEEPSEEK_API_KEY' || reference.startsWith('DAFEIYU_PROVIDER_DEEPSEEK_')
    || selectors.filter(Boolean).includes(reference);
}
