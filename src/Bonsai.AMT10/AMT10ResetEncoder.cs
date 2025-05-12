using System;
using System.ComponentModel;
using System.IO.Ports;
using System.Reactive.Linq;
using System.Text.RegularExpressions;

namespace Bonsai.AMT10
{
    /// <summary>
    /// Provides an operator that resets an AMT10 quadrature encoder counter to zero.
    /// </summary>
    [Description("Sends a command to reset an AMT10 quadrature encoder counter to zero.")]
    public class AMT10ResetEncoder : Sink<object>
    {
        /// <summary>
        /// Gets or sets the name of the serial port.
        /// </summary>
        [Description("The name of the serial port connected to the Arduino.")]
        public string PortName { get; set; }
        
        /// <summary>
        /// Gets or sets the baud rate for the serial port.
        /// </summary>
        [Description("The baud rate of the serial port (default is 9600).")]
        public int BaudRate { get; set; } = 9600;
        
        /// <summary>
        /// Gets or sets the timeout for serial communication in milliseconds.
        /// </summary>
        [Description("The timeout for serial communication in milliseconds.")]
        public int Timeout { get; set; } = 500;

        /// <summary>
        /// Sends the reset command to the encoder whenever the observable sequence emits a notification.
        /// </summary>
        /// <typeparam name="TSource">The type of the elements in the source sequence.</typeparam>
        /// <param name="source">The sequence of notifications used to trigger the reset command.</param>
        /// <returns>The source sequence.</returns>
        public override IObservable<object> Process(IObservable<object> source)
        {
            return source.Do(input =>
            {
                try
                {
                    using (var serialPort = new SerialPort(PortName, BaudRate)
                    {
                        DtrEnable = true,
                        RtsEnable = true,
                        ReadTimeout = Timeout,
                        WriteTimeout = Timeout,
                        NewLine = "\n"
                    })
                    {
                        serialPort.Open();
                        Console.WriteLine("Sending reset command to encoder");
                        serialPort.WriteLine("2"); // Clear encoder command
                        
                        // Wait for a response to confirm the reset
                        int attempts = 0;
                        while (attempts < 50)
                        {
                            try
                            {
                                string response = serialPort.ReadLine().TrimEnd('\r', '\n');
                                
                                // Check for expected response format with Count field
                                Match match = Regex.Match(response, ";Count:(-?\\d+)");
                                if (match.Success)
                                {
                                    int count = int.Parse(match.Groups[1].Value);
                                    if (Math.Abs(count) < 1000)
                                    {
                                        Console.WriteLine($"Encoder reset successful. Count: {count}");
                                        break;
                                    }
                                }
                            }
                            catch (TimeoutException)
                            {
                                // Continue trying on timeout
                            }
                            
                            attempts++;
                        }
                        
                        if (attempts >= 50)
                        {
                            Console.WriteLine("Warning: Could not confirm encoder reset");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error resetting encoder: {ex.Message}");
                }
            });
        }
    }
}