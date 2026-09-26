# Dahua Attendance Management System

An internship learning project developed to explore attendance-device integration using a Dahua attendance terminal, a React frontend, and an ASP.NET Core backend.

The project was created for hands-on learning during my internship and was not a production system used by the company.

## Project Overview

The system explores communication between a web application and a Dahua attendance terminal.

The application includes workflows for:

- User authentication
- Dahua device connection
- Device user retrieval
- Synchronizing device users with a database
- Attendance user management
- Face enrollment
- Face record management
- Device capability checking
- Attendance-related backend operations

## Architecture

The project is divided into two main parts:

```text
Dahua-Attendance-Management-System/
├── frontend/
└── backend/
```

### Frontend

The frontend was developed using:

- React
- JavaScript
- JSX
- Vite
- CSS

The frontend contains pages for:

- Login
- Dashboard
- Device management
- User management
- Face enrollment

API communication is handled through a reusable frontend API service.

### Backend

The backend was developed using:

- C#
- ASP.NET Core
- Entity Framework Core
- SQL Server
- JWT Authentication
- SignalR
- Dahua Device SDK integration

The backend is organized into:

- Controllers
- Services
- Models
- Database context
- Entity Framework migrations
- SignalR hubs

## Dahua Device Integration

The project contains backend services for communicating with a compatible Dahua attendance terminal.

Supported learning workflows include:

- Connecting to a device using its network address
- Retrieving device users
- Synchronizing users with the database
- Checking device capabilities
- Managing attendance-related users
- Face enrollment and face-record operations

The official Dahua SDK binaries are **not included** in this repository.

A compatible Dahua Device Network SDK and supported attendance device are required to use the hardware-integration features.

## Database

The backend uses SQL Server with Entity Framework Core.

The repository includes database models and migrations used during development.

The database itself and real attendance/user records are not included.

## Security

Sensitive development information has been removed from the public repository.

The repository does not include:

- Real device credentials
- Real administrator passwords
- JWT signing secrets
- Enrolled face images
- Employee attendance data
- Database passwords
- Dahua SDK binaries

Configuration placeholders should be replaced with local development values before running the application.

## Project Structure

```text
frontend/
├── src/
│   ├── components/
│   ├── pages/
│   ├── services/
│   └── styles/
├── package.json
└── vite.config.js

backend/
├── Controllers/
├── Data/
├── Hubs/
├── Migrations/
├── Models/
├── Properties/
├── Services/
├── wwwroot/
├── AppServices.cs
├── Program.cs
└── DahuaAttendanceAPI.csproj
```

## Running the Frontend

From the `frontend` directory:

```bash
npm install
npm run dev
```

## Running the Backend

The backend requires:

- .NET
- SQL Server
- Local configuration
- Dahua SDK for hardware integration
- A compatible Dahua attendance terminal for device-related features

Open the backend project in Visual Studio or run it using the .NET CLI after configuring the required dependencies.

## Project Background

**Internship Learning Project**

This project was developed during my internship as a practical learning exercise.

The company already had its own attendance-management solution. This implementation was created separately to help me gain practical experience with:

- Frontend and backend integration
- REST APIs
- Authentication
- Database operations
- Hardware/device communication
- Dahua SDK integration
- Face-enrollment workflows

AI-assisted development was used during parts of the learning and implementation process.

## Project Status

**Incomplete / Development Ended**

Development stopped when the internship period ended.

Some functionality depends on:

- A compatible Dahua attendance terminal
- Dahua SDK installation
- SQL Server database configuration
- Backend configuration
- Local network/device access

The repository is preserved as an internship learning project rather than a completed production system.

## Author

**Muhammad Anas Khan Farooq**

BS Information Technology
