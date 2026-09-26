function StatusBadge({ status }) {
  const normalized = (status || '').toString().trim().toLowerCase()

  let variant = 'neutral'
  if (['enrolled', 'complete', 'completed', 'success', 'active', 'registered', 'yes', 'true'].includes(normalized)) {
    variant = 'success'
  } else if (['pending', 'in progress', 'partial', 'partially enrolled'].includes(normalized)) {
    variant = 'warning'
  } else if (['failed', 'error', 'rejected'].includes(normalized)) {
    variant = 'danger'
  } else if (['not enrolled', 'unenrolled', 'none', 'no', 'false', ''].includes(normalized)) {
    variant = 'neutral'
  }

  const displayText = status ? String(status) : 'Not Enrolled'

  return (
    <span className={`status-badge status-badge-${variant}`}>
      <span className="badge-dot" />
      {displayText}
    </span>
  )
}

export default StatusBadge
