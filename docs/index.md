# Bonsai.AMT10 Documentation

A Bonsai library for interfacing with AMT10 series quadrature encoders via Arduino.

## Introduction

The Bonsai.AMT10 package provides an easy way to read position data from AMT10 series quadrature encoders that are connected to an Arduino running the LS7366R_quadrature_counter firmware. This encoder setup is used in the Allen Institute's camstim software.

## Key Components

### AMT10EncoderSource

This node generates a continuous stream of encoder readings including:
- Index counter
- Raw count value
- Angle in degrees
- Raw data string

### AMT10ResetEncoder

This node allows you to reset the encoder counter to zero, useful when you need to establish a reference position.

## Example Workflows

See the [examples directory](https://github.com/yourusername/Bonsai.AMT10/tree/main/examples) for sample workflows.

## Getting Started

1. Install the Bonsai.AMT10 package in Bonsai
2. Connect your Arduino with the LS7366R counter chip to your computer
3. Use the AMT10EncoderSource node in your workflow
4. Configure the port name to match your Arduino's serial port

## Protocol Description

The extension implements the same protocol used in the camstim library's AMT10_quadrature_encoder.py file:

- **Initialization Sequence**: Sends a series of commands (0, 7, 3, 8, 2, 5) to initialize the encoder
- **Data Format**: Arduino responses follow the format `;Index:123;Count:456`
- **Reset Command**: Send "2" to clear the encoder counter

## Hardware Requirements

- Arduino board
- LS7366R quadrature counter chip
- AMT10 series encoder
- Proper connections between components