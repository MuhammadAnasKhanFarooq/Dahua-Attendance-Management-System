function Dashboard() {
  const cards = [
    ['Database Users', 'API connection will be added next'],
    ['Device Status', 'Live device status will be connected next'],
    ['Face Enrollment', 'Enrollment statistics will be connected next'],
    ['Attendance Today', 'Attendance API will be connected next'],
  ]
  return (
    <>
      <section className="page-heading">
        <div><span className="eyebrow">Overview</span><h1>Attendance Dashboard</h1>
        <p>Manage your Dahua attendance system from one place.</p></div>
        <span className="connection-badge"><span className="status-dot" />Frontend foundation</span>
      </section>
      <section className="stats-grid">
        {cards.map(([label, note]) => (
          <article className="stat-card" key={label}>
            <span>{label}</span><strong>—</strong><small>{note}</small>
          </article>
        ))}
      </section>
      <section className="welcome-card">
        <div><span className="eyebrow">Foundation complete</span>
        <h2>Frontend is ready for backend integration.</h2>
        <p>The application structure, navigation, styling foundation and dashboard shell are in place. Real API data will be connected feature by feature.</p></div>
        <div className="welcome-mark">D</div>
      </section>
    </>
  )
}
export default Dashboard
