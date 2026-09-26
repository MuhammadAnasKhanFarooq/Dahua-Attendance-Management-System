const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || ''
const TOKEN_STORAGE_KEY = 'auth_token'

// --- JWT Token Management -----
export function getStoredToken() {
  const raw = localStorage.getItem(TOKEN_STORAGE_KEY)
  // Normalize common invalid values that may be present as strings
  if (!raw) return null
  if (raw === 'undefined' || raw === 'null') return null
  return raw
}

export function setStoredToken(token) {
  if (token) {
    localStorage.setItem(TOKEN_STORAGE_KEY, token)
    // signal other windows/components that a login occurred
    try { window.dispatchEvent(new Event('auth:login')) } catch {}
  } else {
    localStorage.removeItem(TOKEN_STORAGE_KEY)
  }
}

export function clearToken() {
  localStorage.removeItem(TOKEN_STORAGE_KEY)
  // notify app that token was cleared (e.g., due to 401)
  try { window.dispatchEvent(new Event('auth:logout')) } catch {}
}

export async function apiRequest(path, options = {}) {
  const { timeout = 30000, timeoutMessage, ...fetchOptions } = options

  const controller = new AbortController()
  const timeoutId = setTimeout(() => {
    controller.abort()
  }, timeout)

  try {
    const token = getStoredToken()
    const headers = {
      ...(fetchOptions.body instanceof FormData ? {} : { 'Content-Type': 'application/json' }),
      ...fetchOptions.headers,
    }
    
    if (token) {
      headers['Authorization'] = `Bearer ${token}`
    }

    const response = await fetch(`${API_BASE_URL}${path}`, {
      ...fetchOptions,
      signal: options.signal || controller.signal,
      headers,
    })
    const type = response.headers.get('content-type') || ''
    let data
    try {
      data = type.includes('application/json') ? await response.json() : await response.text()
    } catch {
      data = null
    }

    if (!response.ok) {
      // Handle 401 Unauthorized - clear token and treat as unauthenticated
      if (response.status === 401) {
        clearToken()
      }
      
      const message =
        (typeof data === 'object' && (data?.message || data?.Message || data?.error || data?.Error)) ||
        (typeof data === 'string' && data.trim().length > 0 && data.trim().length < 300 ? data.trim() : null) ||
        (response.status === 403 ? 'Not authorized to access this resource' : null) ||
        `Request failed with status ${response.status}`
      const error = new Error(message)
      error.status = response.status
      error.data = data
      throw error
    }
    return data
  } catch (err) {
    if (err.name === 'AbortError' || err.name === 'TimeoutError') {
      const error = new Error(
        timeoutMessage || 'Request timed out. The terminal/backend did not respond.'
      )
      error.isTimeout = true
      throw error
    }
    throw err
  } finally {
    clearTimeout(timeoutId)
  }
}

export async function getDatabaseUsers() {
  return apiRequest('/api/Attendance/database-users')
}

export async function deleteAttendanceUser(id) {
  return apiRequest(`/api/Attendance/${encodeURIComponent(id)}`, {
    method: 'DELETE',
    timeout: 30000,
    timeoutMessage: 'User deletion request timed out. The backend or terminal did not respond.',
  })
}

export async function registerAttendanceUser(fields) {
  const formData = new FormData()
  Object.entries(fields).forEach(([key, value]) => {
    if (value !== undefined && value !== null && value !== '') {
      formData.append(key, value)
    }
  })

  return apiRequest('/api/Attendance/register', {
    method: 'POST',
    body: formData,
    timeout: 60000,
    timeoutMessage: 'Registration request timed out. The backend or terminal did not respond.',
  })
}

export async function loginDevice({ ip, port, username, password }) {
  return apiRequest('/api/dahua/login', {
    method: 'POST',
    body: JSON.stringify({
      Ip: ip,
      Port: Number(port) || 80,
      Username: username,
      Password: password,
    }),
  })
}

export async function getDeviceUsers(offset = 0, count = 50) {
  return apiRequest(`/api/dahua/users?offset=${offset}&count=${count}`)
}

export async function syncUsersToDatabase(offset = 0, count = 100) {
  return apiRequest(`/api/Attendance/sync-users?offset=${offset}&count=${count}`)
}

export async function getDeviceCapabilities() {
  return apiRequest('/api/dahua/access-person-collection/capabilities')
}

export async function logoutDevice() {
  return apiRequest('/api/Attendance/logout')
}

export async function enableFaceCollection(userId, enable = true) {
  return apiRequest(
    `/api/Attendance/face-collection/enable?userId=${encodeURIComponent(userId)}&enable=${enable}`,
    {
      method: 'POST',
      timeout: 20000,
      timeoutMessage: 'Face collection request timed out. The terminal did not respond.',
    }
  )
}

export async function enrollFace({ userId, photo }, options = {}) {
  const formData = new FormData()
  formData.append('userId', userId)
  formData.append('photo', photo)

  return apiRequest('/api/Attendance/enroll-face', {
    method: 'POST',
    body: formData,
    timeout: 30000,
    timeoutMessage: 'Enrollment request timed out. The terminal/backend did not respond.',
    ...options,
  })
}

// --- Two-Face Management (AttendanceUserFaces API) -------------------------
// userId is treated as a string (never assumed numeric) via encodeURIComponent.

export async function getUserFaces(userId, options = {}) {
  return apiRequest(`/api/Attendance/${encodeURIComponent(userId)}/faces`, {
    timeout: 20000,
    timeoutMessage: 'Face records request timed out. The backend did not respond.',
    ...options,
  })
}

export async function addUserFace({ userId, photo }, options = {}) {
  const formData = new FormData()
  formData.append('photo', photo)

  return apiRequest(`/api/Attendance/${encodeURIComponent(userId)}/faces`, {
    method: 'POST',
    body: formData,
    timeout: 30000,
    timeoutMessage: 'Face upload request timed out. The backend did not respond.',
    ...options,
  })
}

export async function replaceUserFace({ userId, faceIndex, photo }, options = {}) {
  const formData = new FormData()
  formData.append('photo', photo)

  return apiRequest(`/api/Attendance/${encodeURIComponent(userId)}/faces/${faceIndex}`, {
    method: 'PUT',
    body: formData,
    timeout: 30000,
    timeoutMessage: 'Face replacement request timed out. The backend did not respond.',
    ...options,
  })
}

export async function deleteUserFace(userId, faceIndex, options = {}) {
  return apiRequest(`/api/Attendance/${encodeURIComponent(userId)}/faces/${faceIndex}`, {
    method: 'DELETE',
    timeout: 20000,
    timeoutMessage: 'Face deletion request timed out. The backend did not respond.',
    ...options,
  })
}

// --- Authentication -----
export async function login(username, password) {
  const response = await apiRequest('/api/auth/login', {
    method: 'POST',
    body: JSON.stringify({ username, password }),
    timeout: 15000,
  })
  
  // Store the JWT token returned from the login endpoint
  const token = response.token || response.Token
  if (token) {
    setStoredToken(token)
  }
  
  return response
}

export function logout() {
  clearToken()
}

export { API_BASE_URL }

