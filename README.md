# Dahua Attendance Management System

A full-stack attendance-management learning project developed during my internship using a **React frontend**, **ASP.NET Core backend**, **SQL Server**, and integration with a **Dahua attendance terminal**.

I worked on this project for **more than one month** as a practical learning exercise focused on understanding how a real attendance-management system can communicate with external hardware, APIs, authentication systems, databases, and a frontend interface.

> **Important:** A large portion of the code in this project was created and refined with substantial AI assistance. My work focused on directing the implementation, configuring the development environment, integrating the different parts of the system, testing functionality, troubleshooting errors, working with the Dahua device/SDK, and understanding how the system components interact.

## Project Overview

The project explores communication between a web application and a Dahua attendance terminal.

It contains a frontend management interface and an ASP.NET Core API responsible for device communication, user management, authentication, database operations, attendance workflows, and face-enrollment features.

The project includes workflows for:

- User authentication
- Dahua device connection
- Device-user retrieval
- Synchronizing device users with a database
- Attendance user management
- Face enrollment
- Face-record management
- Device capability checking
- Attendance-related backend operations

## Architecture

The project is divided into two main parts:

```text
Dahua-Attendance-Management-System/
├── frontend/
└── backend/
```

## Frontend

The frontend was built with:

- React
- JavaScript
- JSX
- Vite
- CSS

The interface contains pages for:

- Login
- Dashboard
- Device management
- User management
- Face enrollment

The frontend communicates with the backend through a reusable API service.

### Frontend Structure

```text
frontend/
├── src/
│   ├── components/
│   ├── pages/
│   ├── services/
│   └── styles/
├── package.json
└── vite.config.js
```

## Backend

The backend was built with:

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
- Device-integration services

### Backend Structure

```text
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

## Dahua Device Integration

One of the main learning goals of the project was understanding how software can communicate with a physical attendance terminal.

The project explores operations such as:

- Connecting to a Dahua attendance terminal
- Providing device network credentials
- Retrieving users stored on the device
- Synchronizing users with the application database
- Checking device capabilities
- Managing attendance users
- Enrolling face images
- Managing stored face records
- Disconnecting from the device

The official **Dahua SDK binaries are not included** in this repository.

A compatible Dahua SDK and attendance terminal are required for the hardware-related functionality.

## Authentication

The application includes authentication using JWT-based backend authentication.

The public repository does not contain the real development JWT signing key or administrator credentials.

Example configuration values are used instead.

## Database

The backend uses:

- SQL Server
- Entity Framework Core
- Database migrations

The project includes database models and migrations created during development.

The actual development database, employee records, attendance records, and other private data are not included.

## Face Enrollment

The project explores face-enrollment workflows between the application, backend, database, and Dahua attendance device.

Face-related functionality includes:

- Uploading a face image
- Associating faces with attendance users
- Managing multiple face records
- Replacing face records
- Removing face records
- Communicating enrollment operations to the backend/device

Real enrolled face images have been removed from the public repository.

## Security and Privacy

Before publishing this project, development-specific and sensitive information was removed.

The public repository does **not** contain:

- Real Dahua device passwords
- Real administrator passwords
- JWT signing secrets
- Employee attendance records
- Enrolled face photographs
- Database passwords
- Private temporary login files
- Dahua SDK binaries
- Generated build folders

## Running the Frontend

Navigate to:

```bash
cd frontend
```

Install dependencies:

```bash
npm install
```

Start the development server:

```bash
npm run dev
```

## Running the Backend

The backend requires:

- .NET
- SQL Server
- Visual Studio or .NET CLI
- Local development configuration
- Dahua SDK for device integration
- A compatible Dahua attendance terminal for hardware functionality

Configuration placeholders must be replaced with local development values before running the full system.

## Development Process

This project was developed over **more than one month** during my internship.

It involved repeated experimentation with:

- React frontend development
- ASP.NET Core APIs
- SQL Server and Entity Framework
- Authentication
- Device communication
- Dahua SDK integration
- API testing
- Frontend/backend integration
- Face-enrollment workflows
- Debugging integration problems
- Cleaning and organizing the final project structure

### AI-Assisted Development

AI tools played a major role in the implementation of this project.

A substantial portion of the source code was generated, modified, or debugged with AI assistance.

My involvement focused on the overall learning and development process: describing the required functionality, setting up and running the project, connecting the different components, testing generated implementations, identifying problems, iterating on solutions, configuring the database and development environment, working with the device integration, and learning how the resulting system worked.

This repository is therefore presented as an **AI-assisted internship learning project**, rather than as a project whose entire codebase was written manually by me.

## Internship Context

This was a personal learning project developed during my internship.

The company already had its own attendance-management solution and this project was **not used as the company's production system**.

Its purpose was to give me practical exposure to technologies and concepts such as:

- Full-stack application structure
- REST APIs
- Authentication
- Database operations
- Hardware integration
- Device SDKs
- React
- ASP.NET Core
- Face-enrollment workflows

## Project Status

**Incomplete / Development Ended**

Development ended when my internship period finished.

Some functionality requires:

- A compatible Dahua attendance terminal
- Dahua SDK installation
- SQL Server configuration
- Local backend configuration
- Access to the device over a network

The repository is preserved as a record of the learning and practical experience gained during the internship.

## Author

**Muhammad Anas Khan Farooq**

BS Information Technology
