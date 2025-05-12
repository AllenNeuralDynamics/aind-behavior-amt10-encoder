using System;

namespace Bonsai.AMT10
{
    /// <summary>
    /// Represents a single reading from an AMT10 quadrature encoder.
    /// </summary>
    public class AMT10EncoderReading
    {
        /// <summary>
        /// Gets or sets the counter index value from the encoder.
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// Gets or sets the raw count value from the encoder.
        /// </summary>
        public int Count { get; set; }

        /// <summary>
        /// Gets or sets the angle in degrees calculated from the count value.
        /// </summary>
        public double Degrees { get; set; }

        /// <summary>
        /// Gets or sets the raw data string received from the Arduino.
        /// </summary>
        public string RawData { get; set; }
        
        /// <summary>
        /// Returns a string representation of the encoder reading.
        /// </summary>
        public override string ToString()
        {
            return $"Index: {Index}, Count: {Count}, Degrees: {Degrees:F2}°";
        }
    }
}