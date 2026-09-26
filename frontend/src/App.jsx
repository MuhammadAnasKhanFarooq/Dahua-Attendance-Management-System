import { useState, useEffect } from 'react'
import { getStoredToken, logout as apiLogout } from './services/api'
import Sidebar from './components/layout/Sidebar'
import Topbar from './components/layout/Topbar'
import Login from './pages/Login'
import Dashboard from './pages/Dashboard'
import Users from './pages/Users'
import Device from './pages/Device'
import FaceEnrollment from './pages/FaceEnrollment'

function App() {
  const [isAuthenticated, setIsAuthenticated] = useState(false)
  const [isCheckingAuth, setIsCheckingAuth] = useState(true)
  const [activePage, setActivePage] = useState('Dashboard')

  // Check if token exists on mount
  useEffect(() => {
    const token = getStoredToken()
    setIsAuthenticated(Boolean(token))
    setIsCheckingAuth(false)
  }, [])

  // Listen for cross-window/component auth events
  useEffect(() => {
    const onLogin = () => setIsAuthenticated(true)
    const onLogout = () => setIsAuthenticated(false)
    window.addEventListener('auth:login', onLogin)
    window.addEventListener('auth:logout', onLogout)
    return () => {
      window.removeEventListener('auth:login', onLogin)
      window.removeEventListener('auth:logout', onLogout)
    }
  }, [])

  const handleLoginSuccess = () => {
    setIsAuthenticated(true)
    setActivePage('Dashboard')
  }

  const handleLogout = () => {
    apiLogout()
    setIsAuthenticated(false)
  }

  if (isCheckingAuth) {
    return (
      <div className="loading-page">
        <div className="loading-spinner" />
        <p>Loading...</p>
      </div>
    )
  }

  if (!isAuthenticated) {
    return <Login onLoginSuccess={handleLoginSuccess} />
  }

  const renderContent = () => {
    switch (activePage) {
      case 'Dashboard':
        return <Dashboard />
      case 'Device':
        return <Device />
      case 'Users':
        return <Users />
      case 'Face Enrollment':
        return <FaceEnrollment />
      default:
        return (
          <section className="placeholder-page">
            <span className="eyebrow">Coming next</span>
            <h1>{activePage}</h1>
            <p>This section is reserved for the real backend-connected screen.</p>
          </section>
        )
    }
  }

  return (
    <div className="app-shell">
      <Sidebar activePage={activePage} onNavigate={setActivePage} onLogout={handleLogout} />
      <div className="app-main">
        <Topbar title={activePage} />
        <main className="page-content">
          {renderContent()}
        </main>
      </div>
    </div>
  )
}

export default App
