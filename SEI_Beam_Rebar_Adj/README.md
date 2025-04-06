# SEI Beam Rebar Adjuster

A WPF application for adjusting rebar positions in Tekla Structures models. This tool helps structural engineers and detailers to efficiently modify rebar endpoints and maintain proper spacing.

## Features

- Interactive rebar selection and adjustment in Tekla Structures models
- Keyboard shortcuts for common operations
- Adjustable gap and offset parameters
- Real-time model updates
- Support for single rebars
- Automatic welding rebar creation
  - Creates two welding rebars at the connection point
  - 100mm length for welding rebars
  - 1x diameter offset from the main rebar
  - Proper alignment with original rebar direction

## Requirements

- .NET Framework 4.8
- Tekla Structures 2024.0
- Windows operating system

## Dependencies

The application relies on the following Tekla Structures assemblies:
- Tekla.Common.Geometry
- Tekla.Structures
- Tekla.Structures.Drawing
- Tekla.Structures.Model
- Tekla.Structures.Service

## Installation

1. Ensure you have Tekla Structures 2024.0 installed on your system
2. Build the solution using Visual Studio
3. Run the compiled executable

## Usage

1. Launch Tekla Structures
2. Start this application
3. Select two rebars in the model
4. Click to select the connection point
5. The application will:
   - Adjust the rebar endpoints to maintain proper spacing
   - Create welding rebars at the connection point if enabled
6. Changes are automatically applied to the model

## Development

This is a WPF application built with:
- C# (.NET Framework 4.8)
- WPF for the user interface
- Tekla Open API for model manipulation

## License

Copyright 2025. All rights reserved.

## Last Updated
April 6, 2025
