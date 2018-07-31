using System;
using System.Collections.Generic;
using JuliusSweetland.OptiKey.Extensions;
using JuliusSweetland.OptiKey.Properties;
using JuliusSweetland.OptiKey.Services;
using log4net;
using Prism.Commands;
using Prism.Mvvm;
using System.IO;

namespace JuliusSweetland.OptiKey.UI.ViewModels.Management
{
    public class MIDIViewModel : BindableBase
    {
        #region Private Member Vars

        private static readonly ILog Log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private const string ExpectedMaryTTSLocationSuffix = @"\bin\marytts-server.bat";

        private IMIDIService midiService;

        #endregion
        
        #region Ctor

        public MIDIViewModel(IMIDIService midiService)
        {
            this.midiService = midiService;
            
            Load();
        }
        
        #endregion
        
        #region Properties

        public List<string> InputDevices
        {
            get { return midiService.GetAvailableInputDevices(); }
        }

        public List<string> OutputDevices
        {
            get { return midiService.GetAvailableOutputDevices(); }
        }

        private string inputDevice;
        public string InputDevice
        {
            get { return inputDevice; }
            set { SetProperty(ref inputDevice, value); }
        }

        private string outputDevice;
        public string OutputDevice
        {
            get { return outputDevice; }
            set { SetProperty(ref outputDevice, value); }
        }

        public DelegateCommand InfoSoundPlayCommand { get; private set; }
        public DelegateCommand KeySelectionSoundPlayCommand { get; private set; }
        public DelegateCommand ErrorSoundPlayCommand { get; private set; }
        public DelegateCommand AttentionSoundPlayCommand { get; private set; }
        public DelegateCommand MultiKeySelectionCaptureStartSoundPlayCommand { get; private set; }
        public DelegateCommand MultiKeySelectionCaptureEndSoundPlayCommand { get; private set; }
        public DelegateCommand MouseClickSoundPlayCommand { get; private set; }
        public DelegateCommand MouseDownSoundPlayCommand { get; private set; }
        public DelegateCommand MouseUpSoundPlayCommand { get; private set; }
        public DelegateCommand MouseDoubleClickSoundPlayCommand { get; private set; }
        public DelegateCommand MouseScrollSoundPlayCommand { get; private set; }
        
        #endregion
        
        #region Methods

        private void Load()
        {

        }

        public void ApplyChanges()
        {
        }

        #endregion
    }
}
