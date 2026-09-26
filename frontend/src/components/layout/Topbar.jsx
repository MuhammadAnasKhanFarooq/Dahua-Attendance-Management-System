function Topbar({ title }) {
  return (
    <header className="topbar">
      <div><span className="breadcrumb">Dahua Attendance /</span><h2>{title}</h2></div>
      <div className="topbar-actions">
        <span className="api-status"><span className="status-dot" />API: localhost:5125</span>
        <div className="profile">A</div>
      </div>
    </header>
  )
}
export default Topbar
