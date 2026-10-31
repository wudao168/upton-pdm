export interface ProductionScanCredentials { username: string; password: string }

interface StoredCredentials { key: CryptoKey; iv: Uint8Array<ArrayBuffer>; encrypted: ArrayBuffer }

export function canRememberProductionScanCredentials() {
  return Boolean(window.crypto?.subtle && window.indexedDB)
}

async function credentialsRecord(value?: StoredCredentials | null): Promise<StoredCredentials | null> {
  const database = await new Promise<IDBDatabase>((resolve, reject) => {
    const request = window.indexedDB.open('upton-production-scan', 1)
    request.onupgradeneeded = () => request.result.createObjectStore('credentials')
    request.onsuccess = () => resolve(request.result)
    request.onerror = () => reject(request.error)
  })
  try {
    return await new Promise((resolve, reject) => {
      const transaction = database.transaction('credentials', value === undefined ? 'readonly' : 'readwrite')
      const store = transaction.objectStore('credentials')
      const request = value === undefined ? store.get('login') : value === null ? store.delete('login') : store.put(value, 'login')
      transaction.oncomplete = () => resolve(value === undefined ? request.result ?? null : value)
      transaction.onabort = () => reject(transaction.error)
      transaction.onerror = () => reject(transaction.error)
    })
  } finally { database.close() }
}

export async function readProductionScanCredentials(): Promise<ProductionScanCredentials | null> {
  if (!canRememberProductionScanCredentials()) return null
  const stored = await credentialsRecord()
  if (!stored) return null
  const data = await window.crypto.subtle.decrypt({ name: 'AES-GCM', iv: stored.iv }, stored.key, stored.encrypted)
  const credentials = JSON.parse(new TextDecoder().decode(data)) as ProductionScanCredentials
  return typeof credentials.username === 'string' && typeof credentials.password === 'string' ? credentials : null
}

export async function saveProductionScanCredentials(credentials: ProductionScanCredentials) {
  const key = await window.crypto.subtle.generateKey({ name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt'])
  const iv = window.crypto.getRandomValues(new Uint8Array(12))
  const encrypted = await window.crypto.subtle.encrypt({ name: 'AES-GCM', iv }, key, new TextEncoder().encode(JSON.stringify(credentials)))
  await credentialsRecord({ key, iv, encrypted })
}

export async function clearProductionScanCredentials() {
  if (window.indexedDB) await credentialsRecord(null)
}
