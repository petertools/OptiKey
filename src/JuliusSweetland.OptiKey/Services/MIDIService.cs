using System;
using System.Collections.Generic;
using System.Linq;
using System.Speech.Synthesis;
using System.Windows;
using System.Net;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using JuliusSweetland.OptiKey.Properties;
using JuliusSweetland.OptiKey.Services.Audio;
using log4net;
using Un4seen.Bass;
using NAudio.Midi;

namespace JuliusSweetland.OptiKey.Services
{
    public class MIDIService : IMIDIService
    {
        #region Constants

        private const string BassRegistrationEmail = "optikeyfeedback@gmail.com";
        private const string BassRegistrationKey = "2X24252025152222";

        #endregion

        #region Private Member Vars

        private static readonly ILog Log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        
        private readonly SpeechSynthesizer speechSynthesiser;
        private readonly SoundPlayerEx maryTtsPlayer;

        private readonly object speakCompletedLock = new object();
        private EventHandler<SpeakCompletedEventArgs> onSpeakCompleted;
        private EventHandler onMaryTtsSpeakCompleted;

        #endregion

        #region Events

        public event EventHandler<Exception> Error;

        #endregion

        #region Ctor

        public MIDIService()
        {
            speechSynthesiser = new SpeechSynthesizer();
            maryTtsPlayer = new SoundPlayerEx();
            BassNet.Registration(BassRegistrationEmail, BassRegistrationKey);
            Bass.BASS_Init(-1, 44100, BASSInit.BASS_DEVICE_DEFAULT, IntPtr.Zero);
            Application.Current.Exit += (sender, args) => Bass.BASS_Free();
        }

        #endregion

        #region Public methods

        public List<string> GetAvailableInputDevices()
        {
            List<string> devices = new List<string>();

            for (int i = 0; i < MidiIn.NumberOfDevices; ++i)
                devices.Add(MidiIn.DeviceInfo(i).ProductName);

            return devices;
        }

        public List<string> GetAvailableOutputDevices()
        {
            List<string> devices = new List<string>();

            for (int i = 0; i < MidiOut.NumberOfDevices; ++i)
                devices.Add(MidiOut.DeviceInfo(i).ProductName);

            return devices;
        }

        public void SendMessage(Byte channel, Byte first, Byte second)
        {
            Log.Debug("Sending MIDI message Ch: " + channel + " - First:" + first + " - Second: " + second);
        }

        private void PublishError(object sender, Exception ex)
        {
            Log.Error("Publishing Error event (if there are any listeners)", ex);
            if (Error != null)
            {
                Error(sender, ex);
            }
        }

        #endregion

        #region Private methods

        #endregion
    }
}
