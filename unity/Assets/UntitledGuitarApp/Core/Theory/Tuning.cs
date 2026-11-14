using System;

namespace UntitledGuitarApp.Core.Theory
{
    /// <summary>Standard tuning E2 A2 D3 G3 B3 E4. String numbers: 1 = high E … 6 = low E (ARCHITECTURE.md).</summary>
    public static class Tuning
    {
        public const int StringCount = 6;

        // Index = string number − 1 (1 = high E).
        static readonly int[] StandardByString = { 64, 59, 55, 50, 45, 40 };

        public static int StandardMidi(int stringNumber)
        {
            CheckString(stringNumber);
            return StandardByString[stringNumber - 1];
        }

        public static double StandardHz(int stringNumber) => Midi.ToHz(StandardMidi(stringNumber));

        internal static void CheckString(int stringNumber)
        {
            if (stringNumber < 1 || stringNumber > StringCount)
                throw new ArgumentOutOfRangeException(nameof(stringNumber), stringNumber, "string number must be 1 (high E) … 6 (low E)");
        }
    }
}
