import { useState, useEffect, useCallback, useMemo, useRef } from 'react'
import {
  getDatabaseUsers,
  enableFaceCollection,
  getUserFaces,
  addUserFace,
  replaceUserFace,
  deleteUserFace,
  API_BASE_URL,
} from '../services/api'
import StatusBadge from '../components/common/StatusBadge'

const FACE_INDICES = [1, 2]

function normalizeFaces(data) {
  if (Array.isArray(data)) return data
  if (data?.Faces) return data.Faces
  if (data?.faces) return data.faces
  if (data?.Data) return data.Data
  if (data?.data) return data.data
  return []
}

// PhotoUrl can be absolute, protocol-relative, or a backend-relative path.
// When the frontend runs against a full API origin, keep that origin prefix.
function resolvePhotoUrl(url) {
  if (!url) return null
  if (/^https?:\/\//i.test(String(url)) || String(url).startsWith('//')) return url
  const path = String(url).startsWith('/') ? url : `/${url}`
  return `${API_BASE_URL}${path}`
}

function isActive(face) {
  // A face slot is occupied when: face.id != null || face.deviceEnrolled === true
  // Face.id is typically 'Id', 'id', 'faceId', or 'FaceId'
  const id = face?.Id ?? face?.id ?? face?.FaceId ?? face?.faceId
  const deviceEnrolled = face?.DeviceEnrolled ?? face?.deviceEnrolled
  return id != null || deviceEnrolled === true
}

function FaceEnrollment() {
  // Database Users State
  const [users, setUsers] = useState([])
  const [loadingUsers, setLoadingUsers] = useState(true)
  const [usersError, setUsersError] = useState(null)
  const [searchQuery, setSearchQuery] = useState('')

  // Currently viewed user in workspace
  const [selectedUser, setSelectedUser] = useState(null)

  // Two-face records keyed by userId
  // facesByUserId: { [userId]: face[] }
  const [facesByUserId, setFacesByUserId] = useState({})
  const [facesLoadingByUserId, setFacesLoadingByUserId] = useState({})
  const [facesErrorByUserId, setFacesErrorByUserId] = useState({})
  // facesBusyByUserId: { [userId]: { '1'|'2'|'add': 'uploading' | 'replacing' | 'deleting' } }
  const [facesBusyByUserId, setFacesBusyByUserId] = useState({})
  // facesFeedbackByUserId: { [userId]: { slot, type: 'success'|'error', message } | null }
  const [facesFeedbackByUserId, setFacesFeedbackByUserId] = useState({})
  const faceFileInputRef = useRef(null)
  const pendingFaceSlotRef = useRef(null)

  // Per-user collection feedback state: { [userId]: { type, message } }
  const [collectionFeedbackByUserId, setCollectionFeedbackByUserId] = useState({})
  const [enablingCollectionUserId, setEnablingCollectionUserId] = useState(null)

  // Load database users
  const fetchUsers = useCallback(async () => {
    setLoadingUsers(true)
    setUsersError(null)
    try {
      const data = await getDatabaseUsers()
      const list = data?.Users ?? data?.users ?? (Array.isArray(data) ? data : [])
      setUsers(list)

      // Keep selected user reference fresh
      if (selectedUser) {
        const selId = selectedUser.UserId ?? selectedUser.userId ?? selectedUser.Id ?? selectedUser.id
        const updated = list.find((u) => (u.UserId ?? u.userId ?? u.Id ?? u.id) === selId)
        if (updated) {
          setSelectedUser(updated)
        }
      }
    } catch (err) {
      setUsersError(err.message || 'Failed to load database users.')
    } finally {
      setLoadingUsers(false)
    }
  }, [selectedUser])

  useEffect(() => {
    fetchUsers()
  }, []) // Initial load

  // Filter users by User ID and Name
  const filteredUsers = useMemo(() => {
    const query = searchQuery.trim().toLowerCase()
    if (!query) return users

    return users.filter((user) => {
      const userId = String(user.UserId ?? user.userId ?? '').toLowerCase()
      const userName = String(user.UserName ?? user.userName ?? '').toLowerCase()
      return userId.includes(query) || userName.includes(query)
    })
  }, [users, searchQuery])

  // Handle user selection (Allowed even while an enrollment is running in the background)
  const handleSelectUser = (user) => {
    setSelectedUser(user)
  }

  // Get identifiers for currently viewed user
  const selectedUserId = selectedUser?.UserId ?? selectedUser?.userId ?? ''
  const selectedUserName = selectedUser?.UserName ?? selectedUser?.userName ?? '—'
  const selectedUserPhone = selectedUser?.PhoneNumber ?? selectedUser?.phoneNumber ?? '—'
  const selectedUserCard = selectedUser?.CardNo ?? selectedUser?.cardNo ?? '—'
  const selectedUserStatus = selectedUser?.EnrollmentStatus ?? selectedUser?.enrollmentStatus

  // Fetch the two face records for a given user
  const fetchUserFaces = useCallback(async (userId, { silent = false } = {}) => {
    if (!userId) return
    if (!silent) {
      setFacesLoadingByUserId((prev) => ({ ...prev, [userId]: true }))
    }
    setFacesErrorByUserId((prev) => ({ ...prev, [userId]: null }))
    try {
      const data = await getUserFaces(userId)
      setFacesByUserId((prev) => ({ ...prev, [userId]: normalizeFaces(data) }))
    } catch (err) {
      setFacesByUserId((prev) => ({ ...prev, [userId]: [] }))
      setFacesErrorByUserId((prev) => ({
        ...prev,
        [userId]: err.message || `Failed to load faces for user "${userId}".`,
      }))
    } finally {
      if (!silent) {
        setFacesLoadingByUserId((prev) => ({ ...prev, [userId]: false }))
      }
    }
  }, [])

  // Load faces whenever the selected user changes
  useEffect(() => {
    if (!selectedUserId) return
    fetchUserFaces(selectedUserId)
  }, [selectedUserId, fetchUserFaces])

  // Derived face data for the currently viewed user
  const currentFaces = selectedUserId ? (facesByUserId[selectedUserId] || []) : []
  const facesLoading = selectedUserId ? Boolean(facesLoadingByUserId[selectedUserId]) : false
  const facesError = selectedUserId ? (facesErrorByUserId[selectedUserId] || null) : null
  const busyForUser = selectedUserId ? (facesBusyByUserId[selectedUserId] || {}) : {}
  const currentFacesFeedback = selectedUserId ? (facesFeedbackByUserId[selectedUserId] || null) : null

  const faceByIndex = useMemo(() => {
    const map = { 1: undefined, 2: undefined }
    currentFaces.forEach((face) => {
      if (!isActive(face)) return
      // Slots are mapped strictly by the API's FaceIndex (1 or 2), never by
      // array position. The first record wins so a slot is never duplicated.
      const idx = Number(face?.FaceIndex ?? face?.faceIndex)
      if ((idx === 1 || idx === 2) && !map[idx]) {
        map[idx] = face
      }
    })
    return map
  }, [currentFaces])
  const activeFaceCount = [1, 2].filter((i) => Boolean(faceByIndex[i])).length
  const canAddFace = activeFaceCount < FACE_INDICES.length

  // Action: Enable Face Collection Mode for the selected user
  const handleEnableCollection = async () => {
    if (!selectedUser || !selectedUserId) return

    const targetId = selectedUserId
    setEnablingCollectionUserId(targetId)

    try {
      const data = await enableFaceCollection(targetId, true)
      const isSuccess = data?.Success ?? data?.success ?? true
      const message = data?.Message ?? data?.message ?? `Face collection enabled for user "${targetId}".`

      setCollectionFeedbackByUserId((prev) => ({
        ...prev,
        [targetId]: {
          type: isSuccess ? 'success' : 'error',
          message,
        },
      }))
    } catch (err) {
      setCollectionFeedbackByUserId((prev) => ({
        ...prev,
        [targetId]: {
          type: 'error',
          message: err.message || `Failed to enable face collection for user "${targetId}".`,
        },
      }))
    } finally {
      setEnablingCollectionUserId(null)
    }
  }

  // Set a per-face feedback message for a specific user
  const setFacesFeedbackFor = (userId, slot, type, message) => {
    setFacesFeedbackByUserId((prev) => ({
      ...prev,
      [userId]: { slot, type, message },
    }))
  }

  // Set (or clear) the busy state for one slot of a specific user
  const setFaceBusyFor = (userId, busyKey, busyValue) => {
    setFacesBusyByUserId((prev) => {
      const current = { ...(prev[userId] || {}) }
      if (busyValue === null) {
        delete current[busyKey]
      } else {
        current[busyKey] = busyValue
      }
      return { ...prev, [userId]: current }
    })
  }

  // Open the hidden file picker for a specific slot.
  // slot: { type: 'add' } | { type: 'replace', faceIndex: 1 | 2 }
  const handleFaceActionClick = (slot) => {
    if (!selectedUserId) return
    if (faceFileInputRef.current) {
      faceFileInputRef.current.value = ''
    }
    pendingFaceSlotRef.current = slot
    faceFileInputRef.current?.click()
  }

  // File selected for Add Face (POST) or Replace Face 1/2 (PUT)
  const handleFaceFileChange = async (e) => {
    const file = e.target.files?.[0]
    const slot = pendingFaceSlotRef.current
    const targetUserId = selectedUserId
    if (!file || !targetUserId || !slot) return
    pendingFaceSlotRef.current = null

    // Slots are independent; guard against a second operation on the same slot.
    const busyKey = slot.faceIndex ? String(slot.faceIndex) : 'add'
    const busyForTarget = facesBusyByUserId[targetUserId] || {}

    if (!file.type.startsWith('image/')) {
      setFacesFeedbackFor(targetUserId, busyKey, 'error', 'Please select a valid image file (.jpg, .jpeg, .png).')
      return
    }

    if (busyForTarget[busyKey]) return

    setFaceBusyFor(targetUserId, busyKey, slot.faceIndex ? 'replacing' : 'uploading')
    setFacesFeedbackByUserId((prev) => ({ ...prev, [targetUserId]: null }))

    try {
      const data = slot.faceIndex
        ? await replaceUserFace({ userId: targetUserId, faceIndex: slot.faceIndex, photo: file })
        : await addUserFace({ userId: targetUserId, photo: file })

      const isSuccess = data?.Success ?? data?.success ?? true
      const message =
        data?.Message ??
        data?.message ??
        (slot.faceIndex
          ? `Face ${slot.faceIndex} updated successfully for user "${targetUserId}".`
          : `Face added successfully for user "${targetUserId}".`)

      setFacesFeedbackFor(targetUserId, busyKey, isSuccess ? 'success' : 'error', message)
      // Always re-sync with the backend so slot occupancy stays accurate.
      fetchUserFaces(targetUserId, { silent: true })
    } catch (err) {
      const isMaxFaces = err?.status === 409
      setFacesFeedbackFor(
        targetUserId,
        busyKey,
        'error',
        isMaxFaces
          ? 'This user already has the maximum of 2 faces.'
          : err.message || 'Face upload failed. Please try again.'
      )
      // Re-sync slot occupancy (e.g., after a 409) so the UI stays truthful.
      fetchUserFaces(targetUserId, { silent: true })
    } finally {
      setFaceBusyFor(targetUserId, busyKey, null)
    }
  }

  // Delete Face N (FaceIndex 1 or 2)
  const handleDeleteFace = async (faceIndex) => {
    const targetUserId = selectedUserId
    if (!targetUserId) return
    const busyKey = String(faceIndex)
    if ((facesBusyByUserId[targetUserId] || {})[busyKey]) return

    const confirmed = window.confirm(
      `Delete Face ${faceIndex} for user "${targetUserId}"?\n\nThis removes the enrolled face from this user's record.`
    )
    if (!confirmed) return

    setFaceBusyFor(targetUserId, busyKey, 'deleting')
    setFacesFeedbackByUserId((prev) => ({ ...prev, [targetUserId]: null }))

    try {
      const data = await deleteUserFace(targetUserId, faceIndex)
      const isSuccess = data?.Success ?? data?.success ?? true
      const message =
        data?.Message ?? data?.message ?? `Face ${faceIndex} deleted successfully for user "${targetUserId}".`

      setFacesFeedbackFor(targetUserId, busyKey, isSuccess ? 'success' : 'error', message)
      // No assumptions about legacy fields: refresh the face API data after delete.
      fetchUserFaces(targetUserId, { silent: true })
    } catch (err) {
      setFacesFeedbackFor(targetUserId, busyKey, 'error', err.message || `Failed to delete Face ${faceIndex}.`)
      fetchUserFaces(targetUserId, { silent: true })
    } finally {
      setFaceBusyFor(targetUserId, busyKey, null)
    }
  }

  // Collection mode feedback for the currently viewed user
  const currentCollectionFeedback = selectedUserId ? collectionFeedbackByUserId[selectedUserId] : null
  const isEnablingCollectionForSelected = enablingCollectionUserId === selectedUserId

  return (
    <div className="face-enrollment-page">
      {/* Header */}
      <section className="page-heading">
        <div>
          <span className="eyebrow">Biometric Management</span>
          <h1>Face Enrollment</h1>
          <p>Select a registered user and manage the two enrolled face records for that account.</p>
        </div>
        <div className="page-heading-actions">
          <button
            className="btn btn-secondary"
            onClick={fetchUsers}
            disabled={loadingUsers}
            title="Refresh database users"
          >
            <span className={`refresh-icon ${loadingUsers ? 'spinning' : ''}`}>↻</span>
            {loadingUsers ? 'Refreshing...' : 'Refresh Users'}
          </button>
        </div>
      </section>

      {/* Main 2-Column Layout */}
      <div className="enrollment-layout-grid">
        {/* Left Column: User Selection (Always Clickable) */}
        <section className="card user-selection-panel">
          <div className="card-header-row">
            <div>
              <h3>Select User</h3>
              <p>Choose an account from the database.</p>
            </div>
            {!loadingUsers && !usersError && (
              <span className="selection-count-badge">
                {searchQuery ? `${filteredUsers.length} of ${users.length}` : `${users.length} Users`}
              </span>
            )}
          </div>

          {/* Search Box */}
          <div className="search-box-compact">
            <span className="search-icon">🔍</span>
            <input
              type="text"
              className="search-input"
              placeholder="Search by ID or Name..."
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

          {/* Users List */}
          {loadingUsers ? (
            <div className="loading-substate">
              <div className="loading-spinner" />
              <p>Loading database users...</p>
            </div>
          ) : usersError ? (
            <div className="error-substate">
              <p>⚠️ {usersError}</p>
              <button className="btn btn-secondary btn-sm" onClick={fetchUsers}>
                Retry
              </button>
            </div>
          ) : filteredUsers.length === 0 ? (
            <div className="empty-substate">
              <p>{searchQuery ? `No users match "${searchQuery}"` : 'No users found in database.'}</p>
              {searchQuery && (
                <button className="btn btn-subtle" onClick={() => setSearchQuery('')}>
                  Clear Search
                </button>
              )}
            </div>
          ) : (
            <div className="user-selectable-list">
              {filteredUsers.map((user, idx) => {
                const uId = user.UserId ?? user.userId ?? user.Id ?? user.id ?? `user-${idx}`
                const uName = user.UserName ?? user.userName ?? '—'
                const uStatus = user.EnrollmentStatus ?? user.enrollmentStatus
                const isSelected = selectedUserId === uId

                return (
                  <div
                    key={uId}
                    className={`user-list-item ${isSelected ? 'active' : ''}`}
                    onClick={() => handleSelectUser(user)}
                    role="button"
                    tabIndex={0}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter' || e.key === ' ') handleSelectUser(user)
                    }}
                  >
                    <div className="user-list-avatar">
                      {String(uName).charAt(0).toUpperCase() || 'U'}
                    </div>
                    <div className="user-list-info">
                      <strong className="user-list-name">{uName}</strong>
                      <span className="user-list-id font-mono">ID: {uId}</span>
                    </div>
                    <div className="user-list-status">
                      <StatusBadge status={uStatus} />
                    </div>
                  </div>
                )
              })}
            </div>
          )}
        </section>

        {/* Right Column: Enrollment Workspace for Selected User */}
        <section className="card enrollment-workspace-panel">
          {!selectedUser ? (
            <div className="no-selection-placeholder">
              <div className="placeholder-icon">👤</div>
              <h3>No User Selected</h3>
              <p>Select a user from the list on the left to configure biometric face enrollment.</p>
            </div>
          ) : (
            <div className="workspace-content">
              {/* Selected User Header Card */}
              <div className="selected-user-banner">
                <div className="selected-user-avatar">
                  {String(selectedUserName).charAt(0).toUpperCase() || 'U'}
                </div>
                <div className="selected-user-details">
                  <div className="selected-user-title-row">
                    <h3>{selectedUserName}</h3>
                    <StatusBadge status={selectedUserStatus} />
                  </div>
                  <div className="selected-user-meta-row">
                    <span>User ID: <strong className="font-mono">{selectedUserId}</strong></span>
                    <span>Card: <strong className="font-mono">{selectedUserCard}</strong></span>
                    <span>Phone: <strong>{selectedUserPhone}</strong></span>
                  </div>
                </div>
              </div>

              {/* Step 1: Enable Face Collection Mode */}
              <div className="enrollment-step-card">
                <div className="step-header">
                  <span className="step-badge">Step 1</span>
                  <div>
                    <h4>Enable Terminal Collection Mode</h4>
                    <p>Send a signal to the terminal to prepare hardware sensors for user "{selectedUserId}".</p>
                  </div>
                </div>

                <div className="step-actions">
                  <button
                    type="button"
                    className="btn btn-secondary"
                    onClick={handleEnableCollection}
                    disabled={isEnablingCollectionForSelected}
                  >
                    <span className={`btn-icon ${isEnablingCollectionForSelected ? 'spinning' : ''}`}>⚙</span>
                    {isEnablingCollectionForSelected ? 'Enabling Collection Mode...' : 'Enable Face Collection'}
                  </button>
                </div>

                {currentCollectionFeedback && (
                  <div
                    className={`step-feedback banner ${
                      currentCollectionFeedback.type === 'success' ? 'banner-success' : 'banner-error'
                    }`}
                  >
                    <span className="banner-icon">
                      {currentCollectionFeedback.type === 'success' ? '✓' : '⚠️'}
                    </span>
                    <div className="banner-content">
                      <p>{currentCollectionFeedback.message}</p>
                    </div>
                  </div>
                )}
              </div>

              {/* Step 2: Manage Face Records (AttendanceUserFaces API) */}
              <div className="enrollment-step-card">
                <div className="step-header">
                  <span className="step-badge">Step 2</span>
                  <div>
                    <h4>Manage Face Records</h4>
                    <p>
                      Each account supports up to two face records (Face 1 and Face 2).
                      A photo can be added, replaced, or deleted independently for either slot.
                    </p>
                  </div>
                </div>

                <div className="faces-section">
                  <div className="faces-section-header">
                    <div>
                      <strong className="faces-section-title">Face Photo Management</strong>
                      <p className="faces-section-subtitle">
                        {activeFaceCount === 0
                          ? 'No active faces yet. Add a face to get started.'
                          : activeFaceCount >= FACE_INDICES.length
                          ? 'Both face slots are occupied (maximum of 2 active faces).'
                          : 'One active face record. One free slot remains.'}
                      </p>
                    </div>
                    <button
                      type="button"
                      className="btn btn-secondary btn-sm"
                      onClick={() => fetchUserFaces(selectedUserId)}
                      disabled={facesLoading || Object.keys(busyForUser).length > 0}
                      title="Refresh face records"
                    >
                      <span className={`refresh-icon ${facesLoading ? 'spinning' : ''}`}>↻</span>
                      Refresh Faces
                    </button>
                  </div>

                  {facesLoading ? (
                    <div className="loading-substate">
                      <div className="loading-spinner" />
                      <p>Loading face records for user "{selectedUserId}"...</p>
                    </div>
                  ) : facesError ? (
                    <div className="error-substate">
                      <p>⚠️ {facesError}</p>
                      <button
                        className="btn btn-secondary btn-sm"
                        onClick={() => fetchUserFaces(selectedUserId)}
                      >
                        Retry
                      </button>
                    </div>
                  ) : (
                    <>
                      <div className="faces-grid">
                        {FACE_INDICES.map((index) => {
                          const face = faceByIndex[index] || null
                          const busyKey = String(index)
                          const isReplacing = busyForUser[busyKey] === 'replacing'
                          const isDeleting = busyForUser[busyKey] === 'deleting'
                          const isBusy = isReplacing || isDeleting
                          const photoUrl = face ? resolvePhotoUrl(face.PhotoUrl ?? face.photoUrl) : null
                          const slotFeedback =
                            currentFacesFeedback && currentFacesFeedback.slot === busyKey
                              ? currentFacesFeedback
                              : null

                          return (
                            <div
                              key={index}
                              className={`face-card ${face ? '' : 'face-card-empty'} ${
                                isBusy ? 'face-card-busy' : ''
                              }`}
                            >
                              <div className="face-card-header">
                                <span className="face-index-badge">Face {index}</span>
                                {face && <span className="face-status-badge">Active</span>}
                              </div>

                              <div className="face-image-area">
                                {face ? (
                                  photoUrl ? (
                                    <>
                                      <img
                                        src={photoUrl}
                                        alt={`Face ${index} for ${selectedUserId}`}
                                        className="face-image"
                                      />
                                      {isBusy && (
                                        <div className="face-image-overlay">
                                          <span className="loading-spinner-sm" />
                                          <span>{isReplacing ? 'Replacing...' : 'Deleting...'}</span>
                                        </div>
                                      )}
                                    </>
                                  ) : (
                                    <div className="face-device-only">
                                      <span className="face-device-only-icon">📷</span>
                                      <strong>Enrolled on device</strong>
                                      <span>Device enrollment — photo unavailable</span>
                                    </div>
                                  )
                                ) : (
                                  <div className="face-empty-placeholder">
                                    <span className="empty-icon">🫥</span>
                                    <span>No face enrolled in this slot.</span>
                                  </div>
                                )}
                              </div>

                <div className="face-actions">
                                {face ? (
                                  <>
                                    <button
                                      type="button"
                                      className="btn btn-secondary btn-sm"
                                      onClick={() => handleFaceActionClick({ type: 'replace', faceIndex: index })}
                                      disabled={isBusy}
                                    >
                                      {isReplacing ? (
                                        <>
                                          <span className="loading-spinner-xs" /> Replacing...
                                        </>
                                      ) : (
                                        'Replace'
                                      )}
                                    </button>
                                    <button
                                      type="button"
                                      className="btn btn-danger btn-sm"
                                      onClick={() => handleDeleteFace(index)}
                                      disabled={isBusy}
                                    >
                                      {isDeleting ? (
                                        <>
                                          <span className="loading-spinner-xs" /> Deleting...
                                        </>
                                      ) : (
                                        'Delete'
                                      )}
                                    </button>
                                  </>
                                ) : canAddFace ? (
                                  <button
                                    type="button"
                                    className="btn btn-primary face-add-btn"
                                    onClick={() => handleFaceActionClick({ type: 'add' })}
                                    disabled={Boolean(busyForUser.add)}
                                  >
                                    {busyForUser.add ? (
                                      <>
                                        <span className="loading-spinner-xs" /> Uploading...
                                      </>
                                    ) : (
                                      '+ Add Face'
                                    )}
                                  </button>
                                ) : (
                                  <span className="face-empty-note">No free slots available.</span>
                                )}
                              </div>

                              {slotFeedback && (
                                <div
                                  className={`face-feedback banner ${
                                    slotFeedback.type === 'success' ? 'banner-success' : 'banner-error'
                                  }`}
                                >
                                  <span className="banner-icon">
                                    {slotFeedback.type === 'success' ? '✓' : '⚠️'}
                                  </span>
                                  <div className="banner-content">
                                    <p>{slotFeedback.message}</p>
                                  </div>
                                </div>
                              )}
                            </div>
                          )
                        })}
                      </div>

                      {currentFacesFeedback && !['1', '2'].includes(currentFacesFeedback.slot) && (
                        <div
                          className={`face-feedback banner ${
                            currentFacesFeedback.type === 'success' ? 'banner-success' : 'banner-error'
                          }`}
                        >
                          <span className="banner-icon">
                            {currentFacesFeedback.type === 'success' ? '✓' : '⚠️'}
                          </span>
                          <div className="banner-content">
                            <p>{currentFacesFeedback.message}</p>
                          </div>
                        </div>
                      )}

                      <input
                        ref={faceFileInputRef}
                        type="file"
                        accept="image/jpeg,image/png,image/jpg"
                        className="hidden-file-input"
                        onChange={handleFaceFileChange}
                      />
                    </>
                  )}
                </div>
              </div>
            </div>
          )}
        </section>
      </div>
    </div>
  )
}

export default FaceEnrollment
