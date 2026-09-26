const navigation = [
  ['Dashboard', '▦'], ['Device', '◉'], ['Users', '♙'],
  ['Face Enrollment', '◎'], ['Attendance', '▤'],
  ['Live Recognition', '◌'], ['Settings', '⚙'],
]

function Sidebar({ activePage, onNavigate, onLogout }) {
  return (
    <aside className="sidebar">
      <div className="brand">
        <div className="brand-mark">D</div>
        <div><strong>Dahua</strong><span>Attendance</span></div>
      </div>
      <nav className="sidebar-nav">
        {navigation.map(([label, icon]) => (
          <button key={label}
            className={`nav-item ${activePage === label ? 'active' : ''}`}
            onClick={() => onNavigate(label)}>
            <span className="nav-icon">{icon}</span><span>{label}</span>
          </button>
        ))}
      </nav>
      <div className="sidebar-footer">
        <button 
          className="btn btn-secondary btn-sm" 
          onClick={onLogout}
          title="Sign out from your account"
        >
          Sign Out
        </button>
      </div>
      <div className="sidebar-status">
        <span className="status-dot" />
        <div><strong>System</strong><span>Frontend ready</span></div>
      </div>
    </aside>
  )
}

export default Sidebar
