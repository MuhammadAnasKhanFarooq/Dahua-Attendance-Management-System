import { useState, useEffect, useCallback, useMemo } from 'react'
import { getDatabaseUsers, getUserFaces, registerAttendanceUser, deleteAttendanceUser, API_BASE_URL } from '../services/api'
import StatusBadge from '../components/common/StatusBadge'

const USER_TYPES = [
  'General User', 'VIP User', 'Guest User', 'Patrol User',
  'Blocklist User', 'Other User', 'Custom User 1', 'Custom User 2',
]

const initialForm = {
  userID: '', name: '', department: '1', scheduleMode: 'Department',
  validFrom: '', validTo: '', permission: 'User', userType: 'General User',
  timesUsed: 'Unlimited', period: '255', holidayPlan: '255',
  appUserName: '', appPassword: '', appRole: 'User', photo: null,
}

function toIsoDate(value) {
  return value ? new Date(value).toISOString() : ''
}

function responseValue(data, ...keys) {
  for (const key of keys) {
    if (data?.[key] !== undefined && data?.[key] !== null) return data[key]
  }
  return undefined
}

function backendErrorMessage(error, fallback) {
  const data = error?.data
  if (typeof data === 'object' && data) {
    return data.DeviceMessage ?? data.deviceMessage ?? data.message ?? data.Message ?? data.error ?? data.Error ?? fallback
  }
  return error?.message || fallback
}

function registrationOutcome(data) {
  const deviceCreated = responseValue(data, 'DeviceUserCreated', 'deviceUserCreated', 'DeviceEnrollmentSucceeded', 'deviceEnrollmentSucceeded')
  const faceEnrolled = responseValue(data, 'FaceEnrolled', 'faceEnrolled', 'FaceEnrollmentSucceeded', 'faceEnrollmentSucceeded')
  const backendMessage = responseValue(data, 'Message', 'message')
  const messageText = typeof backendMessage === 'string' ? backendMessage.toLowerCase() : ''
  const messages = []
  if (deviceCreated === false || (deviceCreated === undefined && /device.*(fail|offline|not logged)|enrollment.*fail/.test(messageText))) messages.push('User saved but device enrollment failed.')
  else if (deviceCreated === true) messages.push('User saved and device enrollment succeeded.')
  else messages.push('User saved successfully.')
  if (faceEnrolled === true) messages.push('Face enrolled successfully.')
  else if (faceEnrolled === false || (faceEnrolled === undefined && /face.*fail/.test(messageText))) messages.push('Face enrollment failed.')
  if (backendMessage && !messages.some((message) => message.toLowerCase() === messageText)) messages.push(backendMessage)
  return messages.join(' ')
}

function formatDate(dateStr) {
  if (!dateStr) return '—'
  const date = new Date(dateStr)
  if (isNaN(date.getTime())) return dateStr
  return date.toLocaleDateString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  })
}

function normalizeFaces(data) {
  if (Array.isArray(data)) return data
  return data?.Faces || data?.faces || data?.Data || data?.data || []
}

function resolvePhotoUrl(url) {
  if (!url) return null
  if (/^https?:\/\//i.test(String(url)) || String(url).startsWith('//')) return url
  const path = String(url).startsWith('/') ? url : `/${url}`
  return `${API_BASE_URL}${path}`
}

function UserPhoto({ photoUrl, userName, onClick }) {
  const [hasError, setHasError] = useState(false)
  const initials = userName?.trim()?.charAt(0)?.toUpperCase() || '?'

  if (!photoUrl || hasError) {
    return <span className="user-photo-placeholder" aria-label={`No photo for ${userName || 'user'}`}>{initials}</span>
  }

  return <button className="user-photo-button" type="button" onClick={onClick} title="View larger photo"><img className="user-photo" src={photoUrl} alt={`${userName || 'User'} face`} onError={() => setHasError(true)} /></button>
}

function Users() {
  const [users, setUsers] = useState([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  const [searchQuery, setSearchQuery] = useState('')
  const [isAddOpen, setIsAddOpen] = useState(false)
  const [form, setForm] = useState(initialForm)
  const [formError, setFormError] = useState('')
  const [submitState, setSubmitState] = useState('idle')
  const [registrationResult, setRegistrationResult] = useState(null)
  const [previewUrl, setPreviewUrl] = useState('')
  const [deletingUserId, setDeletingUserId] = useState(null)
  const [deleteFeedback, setDeleteFeedback] = useState(null)
  const [photosByDatabaseId, setPhotosByDatabaseId] = useState({})
  const [previewPhoto, setPreviewPhoto] = useState(null)

  const fetchUsers = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const data = await getDatabaseUsers()
      const list = data?.Users || data?.users || (Array.isArray(data) ? data : [])
      setUsers(list)
    } catch (err) {
      setError(err.message || 'Failed to load users from database.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    fetchUsers()
  }, [fetchUsers])

  useEffect(() => {
    let cancelled = false
    const loadPhotos = async () => {
      if (!users.length) {
        setPhotosByDatabaseId({})
        return
      }
      const entries = await Promise.all(users.map(async (user) => {
        const databaseId = user.Id ?? user.id
        const userId = user.UserId ?? user.userId
        if (databaseId === undefined || databaseId === null || !userId) return null
        try {
          const faces = normalizeFaces(await getUserFaces(userId))
          const face = faces.find((item) => item?.PhotoUrl || item?.photoUrl)
          const photoUrl = resolvePhotoUrl(face?.PhotoUrl ?? face?.photoUrl)
          return [String(databaseId), photoUrl]
        } catch {
          return [String(databaseId), null]
        }
      }))
      if (!cancelled) setPhotosByDatabaseId(Object.fromEntries(entries.filter(Boolean)))
    }
    loadPhotos()
    return () => { cancelled = true }
  }, [users])

  const filteredUsers = useMemo(() => {
    const query = searchQuery.trim().toLowerCase()
    if (!query) return users

    return users.filter((user) => {
      const userId = String(user.UserId ?? user.userId ?? '').toLowerCase()
      const userName = String(user.UserName ?? user.userName ?? '').toLowerCase()
      return userId.includes(query) || userName.includes(query)
    })
  }, [users, searchQuery])

  const updateForm = (event) => {
    const { name, value } = event.target
    setForm((current) => ({ ...current, [name]: value }))
    setFormError('')
  }

  const closeAddDialog = () => {
    if (submitState === 'submitting') return
    setIsAddOpen(false)
    setFormError('')
    setRegistrationResult(null)
    setForm(initialForm)
    setPreviewUrl('')
  }

  const handlePhotoChange = (event) => {
    const file = event.target.files?.[0]
    if (!file) return
    if (!file.type.startsWith('image/')) {
      setForm((current) => ({ ...current, photo: null }))
      setFormError('Please select a valid image file.')
      setPreviewUrl('')
      return
    }
    setForm((current) => ({ ...current, photo: file }))
    setPreviewUrl(URL.createObjectURL(file))
    setFormError('')
  }

  const validateForm = () => {
    if (!form.userID.trim()) return 'User ID is required.'
    if (!form.name.trim()) return 'Name is required.'
    if (!/^(?:[1-9]|1[0-9]|20)$/.test(form.department)) return 'Department must be between 1 and 20.'
    if (!['Department', 'Personal'].includes(form.scheduleMode)) return 'Choose a valid schedule mode.'
    if (!['User', 'Admin'].includes(form.permission)) return 'Choose a valid device permission.'
    if (!USER_TYPES.includes(form.userType)) return 'Choose a valid user type.'
    if (!/^(?:0|[1-9][0-9]?|1[0-9][0-9]|2[0-4][0-9]|25[0-5])$/.test(form.period)) return 'Period must be between 0 and 255.'
    if (!/^(?:0|[1-9][0-9]?|1[0-9][0-9]|2[0-4][0-9]|25[0-5])$/.test(form.holidayPlan)) return 'Holiday plan must be between 0 and 255.'
    if ((form.validFrom && !form.validTo) || (!form.validFrom && form.validTo)) return 'Choose both validity dates.'
    if (form.validFrom && form.validTo && new Date(form.validFrom) > new Date(form.validTo)) return 'Validity start must be before or equal to the end.'
    if ((form.appPassword && !form.appUserName.trim()) || (!form.appPassword && form.appUserName.trim())) return 'Application username and password must be supplied together.'
    if (form.appPassword && (form.appPassword.length < 6 || !/[A-Z]/.test(form.appPassword) || !/[a-z]/.test(form.appPassword) || !/[0-9]/.test(form.appPassword) || !/[^A-Za-z0-9]/.test(form.appPassword))) return 'Application password must be at least 6 characters and include uppercase, lowercase, number, and symbol.'
    if (form.appRole === 'Admin' && form.permission !== 'Admin') return 'Only an Admin device permission can create an Admin application account.'
    return ''
  }

  const handleSubmit = async (event, addMore = false) => {
    event.preventDefault()
    const validationError = validateForm()
    if (validationError) {
      setFormError(validationError)
      return
    }
    setSubmitState('submitting')
    setFormError('')
    try {
      const data = await registerAttendanceUser({
        userID: form.userID.trim(), name: form.name.trim(), department: form.department,
        scheduleMode: form.scheduleMode, validFrom: toIsoDate(form.validFrom), validTo: toIsoDate(form.validTo),
        permission: form.permission, userType: form.userType, timesUsed: form.timesUsed,
        period: form.period, holidayPlan: form.holidayPlan, photo: form.photo,
        appUserName: form.appUserName.trim(), appPassword: form.appPassword, appRole: form.appRole,
      })
      setRegistrationResult({ type: 'success', message: registrationOutcome(data) })
      try {
        await fetchUsers()
      } catch {
        setError('Registration succeeded, but the user list could not be refreshed.')
      }
      if (addMore) {
        setForm(initialForm)
        setPreviewUrl('')
      } else {
        setIsAddOpen(false)
        setForm(initialForm)
        setPreviewUrl('')
      }
    } catch (err) {
      const message = err.status === 409
        ? (err.message || 'User already exists, the face is duplicated, or the application username is already in use.')
        : err.status === 401 || err.status === 403
          ? 'You are not authorized to register users.'
          : err.status === 400
            ? (err.message || 'Please check the registration fields.')
            : err.status >= 500
              ? 'Registration failed because the server could not complete the request.'
              : (err.message || 'Registration failed. Please try again.')
      setFormError(message)
    } finally {
      setSubmitState('idle')
    }
  }

  const handleDelete = async (user) => {
    const databaseId = user.Id ?? user.id
    if (databaseId === undefined || databaseId === null || deletingUserId !== null) return
    if (!window.confirm('Are you sure you want to delete this user?')) return

    setDeletingUserId(databaseId)
    setDeleteFeedback(null)
    try {
      await deleteAttendanceUser(databaseId)
      setUsers((current) => current.filter((item) => (item.Id ?? item.id) !== databaseId))
      setDeleteFeedback({ type: 'success', message: 'User deleted successfully.' })
      await fetchUsers()
    } catch (err) {
      const message = err.status === 401
        ? 'Your session has expired. Please sign in again.'
        : err.status === 403
          ? 'You are not authorized to delete users.'
          : err.status === 404
            ? 'User not found.'
            : err.status === 500 || err.status === 503
              ? backendErrorMessage(err, 'The backend could not delete this user.')
              : backendErrorMessage(err, 'User deletion failed. Please try again.')
      setDeleteFeedback({ type: 'error', message })
    } finally {
      setDeletingUserId(null)
    }
  }

  return (
    <div className="users-page">
      <section className="page-heading">
        <div>
          <span className="eyebrow">Database Records</span>
          <h1>Users</h1>
          <p>View and manage all registered users in the database.</p>
        </div>
        <div className="page-heading-actions">
          <button className="btn btn-primary" onClick={() => { setIsAddOpen(true); setRegistrationResult(null); setFormError('') }}>
            + Add User
          </button>
          <button
            className="btn btn-secondary"
            onClick={fetchUsers}
            disabled={loading}
            title="Refresh users list"
          >
            <span className={`refresh-icon ${loading ? 'spinning' : ''}`}>↻</span>
            {loading ? 'Refreshing...' : 'Refresh'}
          </button>
        </div>
      </section>
      {registrationResult && !isAddOpen && <div className="banner banner-success"><span className="banner-icon">✓</span><div className="banner-content"><strong>Registration complete</strong><p>{registrationResult.message}</p></div></div>}
      {deleteFeedback && <div className={`banner banner-${deleteFeedback.type}`}><span className="banner-icon">{deleteFeedback.type === 'success' ? '✓' : '⚠'}</span><div className="banner-content"><strong>{deleteFeedback.type === 'success' ? 'User deleted' : 'Deletion failed'}</strong><p>{deleteFeedback.message}</p></div></div>}

      {/* Toolbar / Search Filter */}
      <section className="users-toolbar card">
        <div className="search-box">
          <span className="search-icon">🔍</span>
          <input
            type="text"
            className="search-input"
            placeholder="Search by User ID or Name..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
          />
          {searchQuery && (
            <button
              type="button"
              className="search-clear-btn"
              onClick={() => setSearchQuery('')}
              title="Clear search"
            >
              ✕
            </button>
          )}
        </div>
        <div className="users-count">
          {!loading && !error && (
            searchQuery ? (
              <span>Showing <strong>{filteredUsers.length}</strong> of {users.length} users</span>
            ) : (
              <span>Total Users: <strong>{users.length}</strong></span>
            )
          )}
        </div>
      </section>

      {/* Content Area: Loading, Error, Empty, or Table */}
      {loading ? (
        <section className="state-card card">
          <div className="loading-spinner" />
          <p className="state-title">Loading users...</p>
          <p className="state-subtitle">Fetching user records from the database API</p>
        </section>
      ) : error ? (
        <section className="state-card state-card-error card">
          <div className="state-icon error-icon">⚠️</div>
          <p className="state-title">Unable to Load Users</p>
          <p className="state-subtitle">{error}</p>
          <button className="btn btn-primary retry-btn" onClick={fetchUsers}>
            Retry Connection
          </button>
        </section>
      ) : users.length === 0 ? (
        <section className="state-card card">
          <div className="state-icon">👥</div>
          <p className="state-title">No Users Found</p>
          <p className="state-subtitle">There are currently no users in the database.</p>
        </section>
      ) : filteredUsers.length === 0 ? (
        <section className="state-card card">
          <div className="state-icon">🔍</div>
          <p className="state-title">No Matching Users</p>
          <p className="state-subtitle">
            No user records matched your search query "{searchQuery}".
          </p>
          <button className="btn btn-secondary" onClick={() => setSearchQuery('')}>
            Clear Search
          </button>
        </section>
      ) : (
        <section className="table-card card">
          <div className="table-wrapper">
            <table className="data-table">
              <thead>
                <tr>
                  <th>Photo</th>
                  <th>User ID</th>
                  <th>Name</th>
                  <th>Phone</th>
                  <th>Card Number</th>
                  <th>Enrollment Status</th>
                  <th>Created Date</th>
                  <th>Actions</th>
                </tr>
              </thead>
              <tbody>
                {filteredUsers.map((user, index) => {
                  const id = user.Id ?? user.id ?? index
                  const databaseId = user.Id ?? user.id
                  const userId = user.UserId ?? user.userId ?? '—'
                  const userName = user.UserName ?? user.userName ?? '—'
                  const phone = user.PhoneNumber ?? user.phoneNumber ?? '—'
                  const cardNo = user.CardNo ?? user.cardNo ?? '—'
                  const enrollmentStatus = user.EnrollmentStatus ?? user.enrollmentStatus
                  const createdAt = user.CreatedAt ?? user.createdAt
                  const photoUrl = photosByDatabaseId[String(databaseId)]

                  return (
                    <tr key={id || `${userId}-${index}`}>
                      <td><UserPhoto photoUrl={photoUrl} userName={userName} onClick={() => setPreviewPhoto({ url: photoUrl, name: userName })} /></td>
                      <td className="font-mono">{userId}</td>
                      <td className="font-medium">{userName}</td>
                      <td>{phone || '—'}</td>
                      <td className="font-mono">{cardNo || '—'}</td>
                      <td>
                        <StatusBadge status={enrollmentStatus} />
                      </td>
                      <td>{formatDate(createdAt)}</td>
                      <td>
                        <button
                          className="btn btn-danger btn-sm"
                          type="button"
                          onClick={() => handleDelete(user)}
                          disabled={deletingUserId !== null}
                          title="Delete user"
                        >
                          {deletingUserId === databaseId ? <><span className="loading-spinner-xs" /> Deleting...</> : 'Delete'}
                        </button>
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        </section>
      )}

      {isAddOpen && (
        <div className="dialog-backdrop" role="presentation">
          <section className="add-user-dialog" role="dialog" aria-modal="true" aria-labelledby="add-user-title">
            <div className="dialog-header">
              <div><span className="eyebrow">Person Management</span><h2 id="add-user-title">Add User</h2></div>
              <button className="dialog-close" type="button" onClick={closeAddDialog} disabled={submitState === 'submitting'} aria-label="Close">✕</button>
            </div>
            {formError && <div className="banner banner-error"><span className="banner-icon">⚠</span><div className="banner-content"><strong>Registration not submitted</strong><p>{formError}</p></div></div>}
            {registrationResult && <div className="banner banner-success"><span className="banner-icon">✓</span><div className="banner-content"><strong>Registration complete</strong><p>{registrationResult.message}</p></div></div>}
            <form onSubmit={handleSubmit}>
              <div className="dialog-section"><h3>Basic Info</h3><div className="add-user-grid">
                <label className="form-group"><span className="form-label">User ID <span className="required">*</span></span><input className="form-input" name="userID" value={form.userID} onChange={updateForm} required /></label>
                <label className="form-group"><span className="form-label">Name <span className="required">*</span></span><input className="form-input" name="name" value={form.name} onChange={updateForm} required /></label>
                <label className="form-group"><span className="form-label">Department</span><select className="form-input" name="department" value={form.department} onChange={updateForm}>{Array.from({ length: 20 }, (_, i) => <option key={i + 1} value={i + 1}>{i + 1}-Default</option>)}</select></label>
                <label className="form-group"><span className="form-label">Schedule Mode</span><select className="form-input" name="scheduleMode" value={form.scheduleMode} onChange={updateForm}><option value="Department">Department Schedule</option><option value="Personal">Personal Schedule</option></select></label>
                <label className="form-group"><span className="form-label">Valid From</span><input className="form-input" type="datetime-local" name="validFrom" value={form.validFrom} onChange={updateForm} /></label>
                <label className="form-group"><span className="form-label">Valid To</span><input className="form-input" type="datetime-local" name="validTo" value={form.validTo} onChange={updateForm} /></label>
                <label className="form-group"><span className="form-label">Permission</span><select className="form-input" name="permission" value={form.permission} onChange={updateForm}><option>User</option><option>Admin</option></select></label>
                <label className="form-group"><span className="form-label">User Type</span><select className="form-input" name="userType" value={form.userType} onChange={updateForm}>{USER_TYPES.map((type) => <option key={type}>{type}</option>)}</select></label>
                <label className="form-group"><span className="form-label">Times Used</span><select className="form-input" name="timesUsed" value={form.timesUsed} onChange={updateForm}><option>Unlimited</option></select></label>
                <label className="form-group"><span className="form-label">Period</span><select className="form-input" name="period" value={form.period} onChange={updateForm}><option value="255">255-Default</option></select></label>
                <label className="form-group"><span className="form-label">Holiday Plan</span><select className="form-input" name="holidayPlan" value={form.holidayPlan} onChange={updateForm}><option value="255">255-Default</option></select></label>
              </div></div>
              <div className="dialog-section"><h3>Verification Mode</h3><div className="verification-options"><span className="verification-option active">Face</span><span className="verification-option">Password</span><span className="verification-option">Card</span><span className="verification-option">Fingerprint</span></div><label className="photo-upload-zone add-photo-zone"><input className="hidden-file-input" type="file" accept="image/*" onChange={handlePhotoChange} /><span className="upload-icon">▧</span><p className="upload-prompt"><strong>{form.photo ? 'Change face image' : 'Select face image'}</strong></p><span className="upload-hint">JPG, JPEG, or PNG</span></label>{previewUrl && <div className="photo-preview-box"><div className="preview-image-container"><img className="preview-image" src={previewUrl} alt="Selected face preview" /></div><div className="preview-details"><strong className="preview-filename">{form.photo?.name}</strong><span className="preview-size">{Math.round(form.photo.size / 1024)} KB</span></div></div>}</div>
              <div className="dialog-section"><h3>Application Login <span className="section-optional">Optional</span></h3><div className="add-user-grid"><label className="form-group"><span className="form-label">Username</span><input className="form-input" name="appUserName" value={form.appUserName} onChange={updateForm} autoComplete="off" /></label><label className="form-group"><span className="form-label">Password</span><input className="form-input" type="password" name="appPassword" value={form.appPassword} onChange={updateForm} autoComplete="new-password" /></label><label className="form-group"><span className="form-label">Role</span><select className="form-input" name="appRole" value={form.appRole} onChange={updateForm}><option>User</option>{form.permission === 'Admin' && <option>Admin</option>}</select></label></div></div>
              <div className="dialog-actions"><button className="btn btn-secondary" type="button" onClick={closeAddDialog} disabled={submitState === 'submitting'}>Cancel</button><button className="btn btn-secondary" type="button" onClick={(event) => handleSubmit(event, true)} disabled={submitState === 'submitting'}>Add More</button><button className="btn btn-primary" type="submit" disabled={submitState === 'submitting'}>{submitState === 'submitting' ? <><span className="loading-spinner-sm" /> Registering...</> : 'Add'}</button></div>
            </form>
          </section>
        </div>
      )}
      {previewPhoto && (
        <div className="photo-modal-backdrop" role="presentation" onClick={() => setPreviewPhoto(null)}>
          <section className="photo-modal" role="dialog" aria-modal="true" aria-label={`${previewPhoto.name} photo`} onClick={(event) => event.stopPropagation()}>
            <div className="dialog-header"><h2>{previewPhoto.name}</h2><button className="dialog-close" type="button" onClick={() => setPreviewPhoto(null)} aria-label="Close photo preview">✕</button></div>
            <img className="photo-modal-image" src={previewPhoto.url} alt={`${previewPhoto.name} face`} onError={() => setPreviewPhoto(null)} />
          </section>
        </div>
      )}
    </div>
  )
}

export default Users
