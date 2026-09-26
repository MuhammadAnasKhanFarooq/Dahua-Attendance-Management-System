import { useState } from 'react'
import {
  loginDevice,
  getDeviceUsers,
  syncUsersToDatabase,
  getDeviceCapabilities,
  logoutDevice,
} from '../services/api'
import StatusBadge from '../components/common/StatusBadge'

function Device() {
  // Connection Form State
  const [credentials, setCredentials] = useState({
    ip: '',
    port: '80',
    username: '',
    password: '',
  })

  // Connection State: 'disconnected' | 'connecting' | 'connected' | 'error'
  const [connectionStatus, setConnectionStatus] = useState('disconnected')
  const [connectionMessage, setConnectionMessage] = useState(null)
  const [connectedDevice, setConnectedDevice] = useState(null)

  // Device Actions State
  const [deviceUsers, setDeviceUsers] = useState(null)
  const [loadingUsers, setLoadingUsers] = useState(false)
  const [usersError, setUsersError] = useState(null)

  const [syncResult, setSyncResult] = useState(null)
  const [syncing, setSyncing] = useState(false)
  const [syncError, setSyncError] = useState(null)

  const [capabilities, setCapabilities] = useState(null)
  const [loadingCapabilities, setLoadingCapabilities] = useState(false)
  const [capabilitiesError, setCapabilitiesError] = useState(null)

  const [loggingOut, setLoggingOut] = useState(false)

  // Handle Input Changes
  const handleInputChange = (e) => {
    const { name, value } = e.target
    setCredentials((prev) => ({ ...prev, [name]: value }))
  }

  // Connect / Login to Device
  const handleConnect = async (e) => {
    e.preventDefault()
    if (!credentials.ip || !credentials.username) {
      setConnectionStatus('error')
      setConnectionMessage('Please provide IP address and username.')
      return
    }

    setConnectionStatus('connecting')
    setConnectionMessage(null)
    setUsersError(null)
    setSyncError(null)
    setCapabilitiesError(null)

    try {
      const data = await loginDevice(credentials)
      const isSuccess = data?.Success ?? data?.success ?? true
      const message = data?.Message ?? data?.message ?? 'Connected to Dahua device successfully.'

      if (isSuccess) {
        setConnectionStatus('connected')
        setConnectionMessage(message)
        setConnectedDevice({
          ip: credentials.ip,
          port: credentials.port,
          username: credentials.username,
          connectedAt: new Date().toLocaleTimeString(),
        })
      } else {
        setConnectionStatus('error')
        setConnectionMessage(message || 'Login failed. Please check device credentials.')
      }
    } catch (err) {
      setConnectionStatus('error')
      setConnectionMessage(err.message || 'Failed to connect to device. Ensure device is reachable.')
    }
  }

  // Load Device Users
  const handleLoadUsers = async () => {
    setLoadingUsers(true)
    setUsersError(null)
    try {
      const data = await getDeviceUsers(0, 50)
      const list = data?.Users ?? data?.users ?? (Array.isArray(data) ? data : [])
      setDeviceUsers(list)
    } catch (err) {
      setUsersError(err.message || 'Failed to load users from device.')
    } finally {
      setLoadingUsers(false)
    }
  }

  // Sync Users to Database
  const handleSyncUsers = async () => {
    setSyncing(true)
    setSyncError(null)
    setSyncResult(null)
    try {
      const data = await syncUsersToDatabase(0, 100)
      setSyncResult(data)
    } catch (err) {
      setSyncError(err.message || 'Failed to sync users to database.')
    } finally {
      setSyncing(false)
    }
  }

  // Get Device Capabilities (Gracefully handles 400 with structured SDK response)
  const handleGetCapabilities = async () => {
    setLoadingCapabilities(true)
    setCapabilitiesError(null)
    try {
      const data = await getDeviceCapabilities()
      const isSuccess = data?.Success ?? data?.success ?? true
      setCapabilities({
        available: isSuccess,
        data,
      })
    } catch (err) {
      // Check if backend returned structured error data in the JSON body
      const responseData = typeof err.data === 'object' && err.data !== null ? err.data : null

      if (responseData) {
        setCapabilities({
          available: false,
          isUnavailableNotice: true,
          data: responseData,
          errorMessage: err.message,
        })
      } else {
        setCapabilitiesError(err.message || 'Failed to retrieve device capabilities.')
      }
    } finally {
      setLoadingCapabilities(false)
    }
  }

  // Logout / Disconnect
  const handleLogout = async () => {
    setLoggingOut(true)
    try {
      await logoutDevice()
    } catch {
      // Proceed with resetting client state even if backend session was already expired
    } finally {
      setLoggingOut(false)
      setConnectionStatus('disconnected')
      setConnectionMessage('Disconnected from device.')
      setConnectedDevice(null)
      setDeviceUsers(null)
      setSyncResult(null)
      setCapabilities(null)
      setUsersError(null)
      setSyncError(null)
      setCapabilitiesError(null)
    }
  }

  const isConnected = connectionStatus === 'connected'

  // Extract capability payload fields safely (handles PascalCase and camelCase)
  const capData = capabilities?.data || {}
  const capSdkError = capData.SdkError ?? capData.sdkError
  const capHexError = capData.HexError ?? capData.hexError
  const capTypeMask = capData.TypeMask ?? capData.typeMask ?? 0
  const capMessage = capData.Message ?? capData.message
  const capFace = capData.Face ?? capData.face ?? false
  const capIdCard = capData.IdCard ?? capData.idCard ?? false
  const capFingerprint = capData.Fingerprint ?? capData.fingerprint ?? false
  const capCard = capData.Card ?? capData.card ?? false
  const capIris = capData.Iris ?? capData.iris ?? false
  const capPalm = capData.Palm ?? capData.palm ?? false
  const capCardReaders = capData.CardReaders ?? capData.cardReaders ?? []
  const capFingerReaders = capData.FingerReaders ?? capData.fingerReaders ?? []

  return (
    <div className="device-page">
      {/* Page Header */}
      <section className="page-heading">
        <div>
          <span className="eyebrow">Hardware Integration</span>
          <h1>Device Management</h1>
          <p>Connect to the Dahua terminal, manage device users, sync records, and check capabilities.</p>
        </div>
        <div className="page-heading-actions">
          <div className="connection-indicator">
            <span
              className={`status-dot ${
                connectionStatus === 'connected'
                  ? 'status-dot-active'
                  : connectionStatus === 'connecting'
                  ? 'status-dot-connecting'
                  : connectionStatus === 'error'
                  ? 'status-dot-error'
                  : 'status-dot-inactive'
              }`}
            />
            <span className="status-label">
              {connectionStatus === 'connected'
                ? 'Connected'
                : connectionStatus === 'connecting'
                ? 'Connecting...'
                : connectionStatus === 'error'
                ? 'Connection Error'
                : 'Disconnected'}
            </span>
          </div>
        </div>
      </section>

      {/* Connection Feedback Message */}
      {connectionMessage && (
        <section
          className={`banner ${
            connectionStatus === 'connected'
              ? 'banner-success'
              : connectionStatus === 'error'
              ? 'banner-error'
              : 'banner-info'
          }`}
        >
          <span className="banner-icon">
            {connectionStatus === 'connected' ? '✓' : connectionStatus === 'error' ? '⚠️' : 'ℹ️'}
          </span>
          <div className="banner-content">
            <strong>
              {connectionStatus === 'connected'
                ? 'Device Connected'
                : connectionStatus === 'error'
                ? 'Connection Notice'
                : 'Status Update'}
            </strong>
            <p>{connectionMessage}</p>
          </div>
        </section>
      )}

      {/* Connection Form or Active Connection Banner */}
      {!isConnected ? (
        <section className="card form-card">
          <div className="card-header">
            <h3>Connect to Dahua Terminal</h3>
            <p>Enter the network address and login credentials of your Dahua attendance terminal.</p>
          </div>
          <form onSubmit={handleConnect} className="device-form">
            <div className="form-grid">
              <div className="form-group">
                <label htmlFor="ip" className="form-label">
                  IP Address <span className="required">*</span>
                </label>
                <input
                  id="ip"
                  name="ip"
                  type="text"
                  className="form-input"
                  placeholder="e.g. 192.168.1.108"
                  value={credentials.ip}
                  onChange={handleInputChange}
                  required
                  disabled={connectionStatus === 'connecting'}
                />
              </div>

              <div className="form-group">
                <label htmlFor="port" className="form-label">
                  Port
                </label>
                <input
                  id="port"
                  name="port"
                  type="number"
                  className="form-input"
                  placeholder="80"
                  value={credentials.port}
                  onChange={handleInputChange}
                  disabled={connectionStatus === 'connecting'}
                />
              </div>

              <div className="form-group">
                <label htmlFor="username" className="form-label">
                  Username <span className="required">*</span>
                </label>
                <input
                  id="username"
                  name="username"
                  type="text"
                  className="form-input"
                  placeholder="e.g. admin"
                  value={credentials.username}
                  onChange={handleInputChange}
                  required
                  disabled={connectionStatus === 'connecting'}
                />
              </div>

              <div className="form-group">
                <label htmlFor="password" className="form-label">
                  Password <span className="required">*</span>
                </label>
                <input
                  id="password"
                  name="password"
                  type="password"
                  className="form-input"
                  placeholder="Device password"
                  value={credentials.password}
                  onChange={handleInputChange}
                  required
                  disabled={connectionStatus === 'connecting'}
                />
              </div>
            </div>

            <div className="form-actions">
              <button
                type="submit"
                className="btn btn-primary"
                disabled={connectionStatus === 'connecting'}
              >
                {connectionStatus === 'connecting' ? (
                  <>
                    <span className="loading-spinner-sm" /> Connecting...
                  </>
                ) : (
                  'Connect to Device'
                )}
              </button>
            </div>
          </form>
        </section>
      ) : (
        <section className="card connected-device-card">
          <div className="connected-device-info">
            <div className="device-avatar">◉</div>
            <div>
              <h3>Dahua Terminal Connected</h3>
              <p className="device-meta">
                <span>IP: <strong>{connectedDevice?.ip}:{connectedDevice?.port}</strong></span>
                <span>User: <strong>{connectedDevice?.username}</strong></span>
                <span>Connected at: <strong>{connectedDevice?.connectedAt}</strong></span>
              </p>
            </div>
          </div>
          <button
            className="btn btn-danger"
            onClick={handleLogout}
            disabled={loggingOut}
            title="Logout from device"
          >
            {loggingOut ? 'Disconnecting...' : 'Disconnect / Logout'}
          </button>
        </section>
      )}

      {/* Device Actions (Shown when connected) */}
      {isConnected && (
        <>
          <section className="device-actions-section">
            <div className="actions-header">
              <h3>Device Operations</h3>
              <p>Execute real-time operations directly on the connected hardware.</p>
            </div>

            <div className="action-buttons-grid">
              <button
                className="btn btn-secondary action-btn"
                onClick={handleLoadUsers}
                disabled={loadingUsers}
              >
                <span className={`btn-icon ${loadingUsers ? 'spinning' : ''}`}>👥</span>
                {loadingUsers ? 'Loading Users...' : 'Load Device Users'}
              </button>

              <button
                className="btn btn-secondary action-btn"
                onClick={handleSyncUsers}
                disabled={syncing}
              >
                <span className={`btn-icon ${syncing ? 'spinning' : ''}`}>🔄</span>
                {syncing ? 'Syncing Users...' : 'Sync Users to DB'}
              </button>

              <button
                className="btn btn-secondary action-btn"
                onClick={handleGetCapabilities}
                disabled={loadingCapabilities}
              >
                <span className={`btn-icon ${loadingCapabilities ? 'spinning' : ''}`}>⚙</span>
                {loadingCapabilities ? 'Fetching...' : 'Get Capabilities'}
              </button>
            </div>
          </section>

          {/* Sync Result Banner */}
          {syncError && (
            <section className="banner banner-error">
              <span className="banner-icon">⚠️</span>
              <div className="banner-content">
                <strong>Sync Error</strong>
                <p>{syncError}</p>
              </div>
            </section>
          )}

          {syncResult && (
            <section className="banner banner-success">
              <span className="banner-icon">✓</span>
              <div className="banner-content">
                <strong>Sync Completed</strong>
                <p>{syncResult?.Message ?? syncResult?.message ?? JSON.stringify(syncResult)}</p>
              </div>
            </section>
          )}

          {/* Capabilities Error (When raw/unstructured failure occurs) */}
          {capabilitiesError && (
            <section className="banner banner-error">
              <span className="banner-icon">⚠️</span>
              <div className="banner-content">
                <strong>Capabilities Error</strong>
                <p>{capabilitiesError}</p>
              </div>
            </section>
          )}

          {/* Capabilities Section (Displays structured response / unavailable info) */}
          {capabilities && (
            <section className="card capabilities-card">
              <div className="card-header">
                <div>
                  <h3>{capabilities.available ? 'Device Capabilities' : 'Capabilities Unavailable'}</h3>
                  <p>
                    {capabilities.available
                      ? 'Hardware features reported by the Dahua terminal.'
                      : 'Access-person collection capability response from Dahua SDK.'}
                  </p>
                </div>
                <button
                  className="btn btn-subtle"
                  onClick={() => setCapabilities(null)}
                  title="Close capabilities panel"
                >
                  ✕ Close
                </button>
              </div>

              {/* Explanatory Notice */}
              {!capabilities.available && (
                <div className="capabilities-notice">
                  <span className="capabilities-notice-icon">ℹ️</span>
                  <div className="capabilities-notice-body">
                    <strong>Terminal Did Not Return Collection Capabilities</strong>
                    <p>
                      The Dahua terminal did not return access-person collection capabilities via this SDK query
                      {capHexError ? ` (SDK Error: ${capHexError})` : ''}.
                      This indicates capability flags are not provided by this firmware version for this command.
                      Other operations (user management, attendance, device sync) remain available.
                    </p>
                  </div>
                </div>
              )}

              {/* SDK Error & TypeMask Info */}
              <div className="cap-meta-grid">
                {capSdkError !== undefined && capSdkError !== null && (
                  <div className="cap-meta-item">
                    <span className="cap-meta-label">SDK Error</span>
                    <span className="cap-meta-value font-mono">{String(capSdkError)}</span>
                  </div>
                )}
                {capHexError && (
                  <div className="cap-meta-item">
                    <span className="cap-meta-label">Hex Error</span>
                    <span className="cap-meta-value font-mono">{String(capHexError)}</span>
                  </div>
                )}
                <div className="cap-meta-item">
                  <span className="cap-meta-label">Type Mask</span>
                  <span className="cap-meta-value font-mono">{String(capTypeMask)}</span>
                </div>
                {capMessage && (
                  <div className="cap-meta-item">
                    <span className="cap-meta-label">Message</span>
                    <span className="cap-meta-value">{String(capMessage)}</span>
                  </div>
                )}
              </div>

              {/* Supported Collection Types */}
              <h4 className="cap-section-title">Supported Collection Types</h4>
              <div className="cap-features-grid">
                <div className="cap-feature-item">
                  <span className="cap-feature-name">Face</span>
                  <span className={`cap-badge ${capFace ? 'cap-badge-yes' : 'cap-badge-no'}`}>
                    {capFace ? 'Yes' : 'No'}
                  </span>
                </div>
                <div className="cap-feature-item">
                  <span className="cap-feature-name">ID Card</span>
                  <span className={`cap-badge ${capIdCard ? 'cap-badge-yes' : 'cap-badge-no'}`}>
                    {capIdCard ? 'Yes' : 'No'}
                  </span>
                </div>
                <div className="cap-feature-item">
                  <span className="cap-feature-name">Fingerprint</span>
                  <span className={`cap-badge ${capFingerprint ? 'cap-badge-yes' : 'cap-badge-no'}`}>
                    {capFingerprint ? 'Yes' : 'No'}
                  </span>
                </div>
                <div className="cap-feature-item">
                  <span className="cap-feature-name">Card</span>
                  <span className={`cap-badge ${capCard ? 'cap-badge-yes' : 'cap-badge-no'}`}>
                    {capCard ? 'Yes' : 'No'}
                  </span>
                </div>
                <div className="cap-feature-item">
                  <span className="cap-feature-name">Iris</span>
                  <span className={`cap-badge ${capIris ? 'cap-badge-yes' : 'cap-badge-no'}`}>
                    {capIris ? 'Yes' : 'No'}
                  </span>
                </div>
                <div className="cap-feature-item">
                  <span className="cap-feature-name">Palm</span>
                  <span className={`cap-badge ${capPalm ? 'cap-badge-yes' : 'cap-badge-no'}`}>
                    {capPalm ? 'Yes' : 'No'}
                  </span>
                </div>
              </div>

              {/* Reader information if available */}
              {(capCardReaders.length > 0 || capFingerReaders.length > 0) && (
                <div className="cap-meta-grid">
                  <div className="cap-meta-item">
                    <span className="cap-meta-label">Card Readers</span>
                    <span className="cap-meta-value">{capCardReaders.length ? capCardReaders.join(', ') : 'None reported'}</span>
                  </div>
                  <div className="cap-meta-item">
                    <span className="cap-meta-label">Fingerprint Readers</span>
                    <span className="cap-meta-value">{capFingerReaders.length ? capFingerReaders.join(', ') : 'None reported'}</span>
                  </div>
                </div>
              )}

              {/* Expandable Raw JSON */}
              <details className="cap-raw-details">
                <summary>View raw SDK payload</summary>
                <pre className="json-viewer">
                  {JSON.stringify(capData, null, 2)}
                </pre>
              </details>
            </section>
          )}

          {/* Device Users Table Section */}
          {usersError && (
            <section className="state-card state-card-error card">
              <div className="state-icon error-icon">⚠️</div>
              <p className="state-title">Failed to Load Device Users</p>
              <p className="state-subtitle">{usersError}</p>
              <button className="btn btn-primary retry-btn" onClick={handleLoadUsers}>
                Retry Loading Users
              </button>
            </section>
          )}

          {loadingUsers && (
            <section className="state-card card">
              <div className="loading-spinner" />
              <p className="state-title">Loading Device Users...</p>
              <p className="state-subtitle">Querying user records directly from the Dahua terminal.</p>
            </section>
          )}

          {deviceUsers && !loadingUsers && (
            <section className="card device-users-card">
              <div className="card-header-row">
                <div>
                  <h3>Terminal Users ({deviceUsers.length})</h3>
                  <p>User accounts currently residing in the terminal memory.</p>
                </div>
                <button
                  className="btn btn-secondary"
                  onClick={handleLoadUsers}
                  disabled={loadingUsers}
                >
                  <span className={`refresh-icon ${loadingUsers ? 'spinning' : ''}`}>↻</span>
                  Refresh List
                </button>
              </div>

              {deviceUsers.length === 0 ? (
                <div className="empty-substate">
                  <p>No user records returned from the device.</p>
                </div>
              ) : (
                <div className="table-wrapper">
                  <table className="data-table">
                    <thead>
                      <tr>
                        <th>User ID</th>
                        <th>Name</th>
                        <th>Card Number</th>
                        <th>Authority / Role</th>
                        <th>Status</th>
                      </tr>
                    </thead>
                    <tbody>
                      {deviceUsers.map((user, idx) => {
                        const userId = user.UserId ?? user.userId ?? user.Id ?? user.id ?? '—'
                        const userName = user.UserName ?? user.userName ?? user.Name ?? user.name ?? '—'
                        const cardNo = user.CardNo ?? user.cardNo ?? user.CardNumber ?? user.cardNumber ?? '—'
                        const role = user.UserType ?? user.userType ?? user.Authority ?? user.authority ?? 'General'
                        const status = user.EnrollmentStatus ?? user.enrollmentStatus ?? 'Active'

                        return (
                          <tr key={userId || idx}>
                            <td className="font-mono">{userId}</td>
                            <td className="font-medium">{userName}</td>
                            <td className="font-mono">{cardNo || '—'}</td>
                            <td>{role}</td>
                            <td>
                              <StatusBadge status={status} />
                            </td>
                          </tr>
                        )
                      })}
                    </tbody>
                  </table>
                </div>
              )}
            </section>
          )}
        </>
      )}
    </div>
  )
}

export default Device
