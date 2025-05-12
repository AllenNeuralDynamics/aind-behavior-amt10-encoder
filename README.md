# Bonsai.AMT10

A [Bonsai](https://bonsai-rx.org/) library for interfacing with AMT10 series quadrature encoders via Arduino.

## Overview

This package provides nodes for reading and interacting with AMT10 quadrature encoders connected through an Arduino running the LS7366R_quadrature_counter firmware. The implementation follows the same protocol used in the camstim library's AMT10_quadrature_encoder.py.

## Installation

To install the Bonsai.AMT10 package:

1. Open Bonsai
2. Click on the "Tools" menu and select "Manage Packages"
3. Click on "Settings" and add the NuGet package source where this package is hosted
4. Search for "Bonsai.AMT10" and install

## Usage

### Requirements

- Arduino board connected to the AMT10 encoder via LS7366R counter chip
- Arduino running the "LS7366R_quadrature_counter.ino" firmware (version v0.1.5 or later)

### Key Components

- **AMT10EncoderSource**: Generates a continuous stream of encoder readings
- **AMT10ResetEncoder**: Resets the encoder counter to zero

### Basic Workflow

```
[AMT10EncoderSource] --> [MemberSelector:Degrees] --> [LineGraph]
```

### Example

1. Add an `AMT10EncoderSource` node to your Bonsai workflow
2. Configure the `PortName` property to match your Arduino's serial port
3. Connect the node to visualization or data processing nodes

## Configuration

### AMT10EncoderSource Properties

- `PortName`: The name of the serial port (e.g., "COM3" on Windows or "/dev/tty.usbmodem1234" on macOS)
- `BaudRate`: The baud rate for serial communication (default: 9600)
- `Timeout`: The timeout for serial operations in milliseconds (default: 500)
- `CountsPerRevolution`: The number of counts per revolution (default: 8192 for AMT10 encoders)
- `Debug`: Enable/disable debug output from the Arduino

### AMT10ResetEncoder Properties

- `PortName`: The name of the serial port
- `BaudRate`: The baud rate for serial communication (default: 9600)
- `Timeout`: The timeout for serial operations in milliseconds (default: 500)

## Protocol Description

The extension communicates with an Arduino running the LS7366R_quadrature_counter firmware. The protocol consists of:

- **Commands**: Single-character commands sent to the Arduino (e.g., "2" to clear the encoder)
- **Responses**: Arduino responses in the format `;Index:123;Count:456`

## License

This project is licensed under the MIT License - see the LICENSE file for details.