using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using JuliusSweetland.OptiKey.Enums;
using JuliusSweetland.OptiKey.Extensions;
using JuliusSweetland.OptiKey.Models;
using JuliusSweetland.OptiKey.Native;
using JuliusSweetland.OptiKey.Properties;
using JuliusSweetland.OptiKey.UI.ViewModels.Keyboards;
using JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.Base;

namespace JuliusSweetland.OptiKey.UI.ViewModels
{
    public partial class MainViewModel
    {
        public void AttachErrorNotifyingServiceHandlers()
        {
            Log.Info("AttachErrorNotifyingServiceHandlers called.");

            if (errorNotifyingServices != null)
            {
                errorNotifyingServices.ForEach(s => s.Error += HandleServiceError);
            }

            Log.Info("AttachErrorNotifyingServiceHandlers complete.");
        }

        private void SetupInputServiceEventHandlers()
        {
            Log.Info("SetupInputServiceEventHandlers called.");

            inputServicePointsPerSecondHandler = (o, value) => { PointsPerSecond = value; };

            inputServiceCurrentPositionHandler = (o, tuple) =>
            {
                CurrentPositionPoint = tuple.Item1;
                CurrentPositionKey = tuple.Item2;

                if (keyStateService.KeyDownStates[KeyValues.MouseMagneticCursorKey].Value.IsDownOrLockedDown()
                    && !keyStateService.KeyDownStates[KeyValues.SleepKey].Value.IsDownOrLockedDown())
                {
                    mouseOutputService.MoveTo(CurrentPositionPoint);
                }
            };

            if (Settings.Default.LookToScrollEnabled)
            {
                inputServiceLivePositionHandler = (o, position) => UpdateLookToScroll(position);
            }

            inputServiceSelectionProgressHandler = (o, progress) =>
            {
                if (progress.Item1 == null
                    && progress.Item2 == 0)
                {
                    ResetSelectionProgress(); //Reset all keys
                }
                else if (progress.Item1 != null)
                {
                    if (SelectionMode == SelectionModes.Key
                        && progress.Item1.KeyValue != null)
                    {
                        keyStateService.KeySelectionProgress[progress.Item1.KeyValue] =
                            new NotifyingProxy<double>(progress.Item2);
                    }
                    else if (SelectionMode == SelectionModes.Point)
                    {
                        PointSelectionProgress = new Tuple<Point, double>(progress.Item1.Point, progress.Item2);
                    }
                }
            };

            inputServiceSelectionHandler = (o, value) =>
            {
                Log.Info("Selection event received from InputService.");

                SelectionResultPoints = null; //Clear captured points from previous SelectionResult event

                if (SelectionMode == SelectionModes.Key
                    && value.KeyValue != null)
                {
                    if (!capturingStateManager.CapturingMultiKeySelection)
                    {
                        audioService.PlaySound(Settings.Default.KeySelectionSoundFile, Settings.Default.KeySelectionSoundVolume);
                    }

                    if (KeySelection != null)
                    {
                        Log.InfoFormat("Firing KeySelection event with KeyValue '{0}'", value.KeyValue);
                        KeySelection(this, value.KeyValue);
                    }
                }
                else if (SelectionMode == SelectionModes.Point)
                {
                    if (PointSelection != null)
                    {
                        PointSelection(this, value.Point);

                        if (nextPointSelectionAction != null)
                        {
                            Log.InfoFormat("Executing nextPointSelectionAction delegate with point '{0}'", value.Point);
                            nextPointSelectionAction(value.Point);
                        }
                    }
                }
            };

            inputServiceSelectionResultHandler = (o, tuple) =>
            {
                Log.Info("SelectionResult event received from InputService.");

                var points = tuple.Item1;
                var singleKeyValue = tuple.Item2;
                var multiKeySelection = tuple.Item3;

                SelectionResultPoints = points; //Store captured points from SelectionResult event (displayed for debugging)

                if (SelectionMode == SelectionModes.Key
                    && (singleKeyValue != null || (multiKeySelection != null && multiKeySelection.Any())))
                {
                    KeySelectionResult(singleKeyValue, multiKeySelection);
                }
                else if (SelectionMode == SelectionModes.Point)
                {
                    //SelectionResult event has no real meaning when dealing with point selection
                }
            };

            Log.Info("SetupInputServiceEventHandlers complete.");
        }

        public void AttachInputServiceEventHandlers()
        {
            Log.Info("AttachInputServiceEventHandlers called.");
            
            inputService.PointsPerSecond += inputServicePointsPerSecondHandler;
            inputService.CurrentPosition += inputServiceCurrentPositionHandler;
            inputService.SelectionProgress += inputServiceSelectionProgressHandler;
            inputService.Selection += inputServiceSelectionHandler;
            inputService.SelectionResult += inputServiceSelectionResultHandler;

            inputService.PointToKeyValueMap = pointToKeyValueMap;
            inputService.SelectionMode = SelectionMode;

            if (Settings.Default.LookToScrollEnabled)
            {
                inputService.LivePosition += inputServiceLivePositionHandler;
            }

            Log.Info("AttachInputServiceEventHandlers complete.");
        }
        

        public void DetachInputServiceEventHandlers()
        {
            Log.Info("DetachInputServiceEventHandlers called.");
            
            inputService.PointsPerSecond -= inputServicePointsPerSecondHandler;
            inputService.CurrentPosition -= inputServiceCurrentPositionHandler;
            inputService.SelectionProgress -= inputServiceSelectionProgressHandler;
            inputService.Selection -= inputServiceSelectionHandler;
            inputService.SelectionResult -= inputServiceSelectionResultHandler;

            if (Settings.Default.LookToScrollEnabled)
            {
                inputService.LivePosition -= inputServiceLivePositionHandler;
            }

            Log.Info("DetachInputServiceEventHandlers complete.");
        }

        private void ProcessChangeKeyboardKeyValue(ChangeKeyboardKeyValue keyValue)
        {
            var currentKeyboard = Keyboard;

            Action backAction = () => { };
            Action exitAction = () => { };
            Action enterAction = () => { };

            // Set up back action
            if (keyValue.Replace)
            {
                var navigableKeyboard = Keyboard as IBackAction;
                if (navigableKeyboard != null && navigableKeyboard.BackAction != null)
                {
                    backAction = navigableKeyboard.BackAction;
                }
            }
            else
            {
                backAction = () =>
                {
                    Keyboard = currentKeyboard;

                    if (!(currentKeyboard is DynamicKeyboard))
                    {
                        mainWindowManipulationService.ResizeDockToFull();
                    }                    
                };
            }

            if (keyValue.BuiltInKeyboard.HasValue)
            {
                SetKeyboardFromEnum(keyValue.BuiltInKeyboard.Value, mainWindowManipulationService, backAction);
            }
            else {
                // Set up new dynamic keyboard

                // Extract any key states or layout overrides if present
                var initialKeyStates = new Dictionary<KeyValue, KeyDownStates>();
                double? overrideHeight = null;
                try
                {
                    XmlKeyboard keyboard = XmlKeyboard.ReadFromFile(keyValue.KeyboardFilename);
                    XmlKeyStates states = keyboard.InitialKeyStates;

                    if (states != null)
                    {
                        foreach (var item in states.GetKeyOverrides())
                        {
                            // TODO: move this into XmlKeyStates.GetKeyOverrides ?
                            FunctionKeys? fKey = FunctionKeysExtensions.FromString(item.Item1);
                            if (fKey.HasValue)
                            {
                                KeyValue val = new KeyValue(fKey.Value);
                                initialKeyStates.Add(val, item.Item2);
                            }
                        }
                    }

                    overrideHeight = keyboard.Height;
                }
                catch (Exception)
                {
                    // will get caught and handled when DynamicKeyboard is created so we are good to ignore here 
                }

                DynamicKeyboard newDynKeyboard = new DynamicKeyboard(backAction, mainWindowManipulationService, keyStateService, keyValue.KeyboardFilename);
                newDynKeyboard.SetKeyOverrides(initialKeyStates);
                newDynKeyboard.OverrideKeyboardLayout(overrideHeight);
                Keyboard = newDynKeyboard;

                // Clear the scratchpad when launching a dynamic keyboard.
                // (scratchpad only supported on single dynamic keyboard currently)
                keyboardOutputService.ProcessFunctionKey(FunctionKeys.ClearScratchpad);
            }
        }
        
        private void ProcessBasicKeyValue(KeyValue singleKeyValue)
        {
            Log.InfoFormat("KeySelectionResult received with string value '{0}' and function key values '{1}'",
                singleKeyValue.String.ToPrintableString(), singleKeyValue.FunctionKey);
          
            keyStateService.ProgressKeyDownState(singleKeyValue);

            if (!string.IsNullOrEmpty(singleKeyValue.String)
                && singleKeyValue.FunctionKey != null)
            {
                HandleStringAndFunctionKeySelectionResult(singleKeyValue);
            }
            else
            {
                if (!string.IsNullOrEmpty(singleKeyValue.String))
                {
                    //Single key string
                    keyboardOutputService.ProcessSingleKeyText(singleKeyValue.String);
                }

                if (singleKeyValue.FunctionKey != null)
                {
                    //Single key function key
                    HandleFunctionKeySelectionResult(singleKeyValue);
                }          
            }
        }
      
	private void KeySelectionResult(KeyValue singleKeyValue, List<string> multiKeySelection)
        {
            // Pass single key to appropriate processing function
            if (singleKeyValue != null)
            {
                ChangeKeyboardKeyValue kv_link = singleKeyValue as ChangeKeyboardKeyValue;

                if (kv_link != null)
                {
                    ProcessChangeKeyboardKeyValue(kv_link);
                }
                else 
                {
                    ProcessBasicKeyValue(singleKeyValue);
                }
            }
            
            
            //Multi key selection
            if (multiKeySelection != null
                && multiKeySelection.Any())
            {
                Log.InfoFormat("KeySelectionResult received with '{0}' multiKeySelection results", multiKeySelection.Count);
                keyboardOutputService.ProcessMultiKeyTextAndSuggestions(multiKeySelection);
            }
        }

        private void HandleStringAndFunctionKeySelectionResult(KeyValue singleKeyValue)
        {
            var currentKeyboard = Keyboard;

            switch (singleKeyValue.FunctionKey.Value)
            {
                case FunctionKeys.CommuniKate:
                    if (singleKeyValue.String.Contains(":action:"))
                    {
                        string[] stringSeparators = new string[] { ":action:" };
                        foreach (var action in singleKeyValue.String.Split(stringSeparators, StringSplitOptions.None).ToList())
                        {
                            Log.DebugFormat("Performing CommuniKate action: {0}.", action);
                            if (action.StartsWith("board:"))
                            {
                                string board = action.Substring(6);
                                switch (board)
                                {
                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.Alpha1":
                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.Alpha2":
                                        Settings.Default.UsingCommuniKateKeyboardLayout = false;
                                        Log.Info("Changing keyboard back to Alpha.");
                                        Keyboard = new Alpha1();
                                        break;

                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.ConversationAlpha1":
                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.ConversationAlpha2":
                                        Settings.Default.UsingCommuniKateKeyboardLayout = false;
                                        Log.Info("Changing keyboard back to Conversation Alpha.");
                                        Action conversationAlphaBackAction = () =>
                                        {
                                            Log.Info("Restoring window size.");
                                            mainWindowManipulationService.Restore();
                                            Settings.Default.UsingCommuniKateKeyboardLayout = Settings.Default.UseCommuniKateKeyboardLayoutByDefault;
                                            Keyboard = new Menu(() => Keyboard = new Alpha1());
                                        };
                                        Keyboard = new ConversationAlpha1(conversationAlphaBackAction);
                                        break;

                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.ConversationConfirm":
                                        Settings.Default.UsingCommuniKateKeyboardLayout = Settings.Default.UseCommuniKateKeyboardLayoutByDefault;
                                        Log.Info("Changing keyboard back to Conversation Confirm.");
                                        Action conversationConfirmBackAction = () =>
                                        {
                                            Log.Info("Restoring window size.");
                                            mainWindowManipulationService.Restore();
                                            Keyboard = new Menu(() => Keyboard = new Alpha1());
                                        };
                                        Keyboard = new ConversationConfirm(conversationConfirmBackAction);
                                        break;

                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.ConversationNumericAndSymbols":
                                        Settings.Default.UsingCommuniKateKeyboardLayout = Settings.Default.UseCommuniKateKeyboardLayoutByDefault;
                                        Log.Info("Changing keyboard back to Conversation Numeric And Symbols.");
                                        Action conversationNumericAndSymbolsBackAction = () =>
                                        {
                                            Log.Info("Restoring window size.");
                                            mainWindowManipulationService.Restore();
                                            Keyboard = new Menu(() => Keyboard = new Alpha1());
                                        };
                                        Keyboard = new ConversationNumericAndSymbols(conversationNumericAndSymbolsBackAction);
                                        break;

                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.Currencies1":
                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.Currencies2":
                                        Settings.Default.UsingCommuniKateKeyboardLayout = Settings.Default.UseCommuniKateKeyboardLayoutByDefault;
                                        Log.Info("Changing keyboard back to Currencies.");
                                        Keyboard = new Currencies1();
                                        break;

                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.Diacritics1":
                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.Diacritics2":
                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.Diacritics3":
                                        Settings.Default.UsingCommuniKateKeyboardLayout = Settings.Default.UseCommuniKateKeyboardLayoutByDefault;
                                        Log.Info("Changing keyboard back to Diacritics.");
                                        Keyboard = new Diacritics1();
                                        break;

                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.Menu":
                                        Settings.Default.UsingCommuniKateKeyboardLayout = Settings.Default.UseCommuniKateKeyboardLayoutByDefault;
                                        Log.Info("Changing keyboard back to Menu.");
                                        if (mainWindowManipulationService.WindowState == WindowStates.Maximised)
                                        {
                                            Log.Info("Restoring window size.");
                                            mainWindowManipulationService.Restore();
                                        }
                                        Keyboard = new Menu(() => Keyboard = new Alpha1());
                                        break;

                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.Mouse":
                                        Settings.Default.UsingCommuniKateKeyboardLayout = Settings.Default.UseCommuniKateKeyboardLayoutByDefault;
                                        Log.Info("Changing keyboard back to Mouse.");
                                        if (mainWindowManipulationService.WindowState == WindowStates.Maximised)
                                        {
                                            Log.Info("Restoring window size.");
                                            mainWindowManipulationService.Restore();
                                        }
                                        Keyboard = new Mouse(() => Keyboard = new Menu(() => Keyboard = new Alpha1()));
                                        break;

                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.NumericAndSymbols1":
                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.NumericAndSymbols2":
                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.NumericAndSymbols3":
                                        Settings.Default.UsingCommuniKateKeyboardLayout = Settings.Default.UseCommuniKateKeyboardLayoutByDefault;
                                        Log.Info("Changing keyboard back to Numeric And Symbols.");
                                        Keyboard = new NumericAndSymbols1();
                                        break;

                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.PhysicalKeys":
                                        Settings.Default.UsingCommuniKateKeyboardLayout = Settings.Default.UseCommuniKateKeyboardLayoutByDefault;
                                        Log.Info("Changing keyboard back to Mouse.");
                                        Keyboard = new PhysicalKeys();
                                        break;

                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.SimplifiedAlpha":
                                        Settings.Default.UsingCommuniKateKeyboardLayout = Settings.Default.UseCommuniKateKeyboardLayoutByDefault;
                                        Log.Info("Changing keyboard back to Simplified Alpha.");
                                        Keyboard = new SimplifiedAlpha(() => Keyboard = new Menu(() => Keyboard = new Alpha1()));
                                        break;

                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.SimplifiedConversationAlpha":
                                        Settings.Default.UsingCommuniKateKeyboardLayout = Settings.Default.UseCommuniKateKeyboardLayoutByDefault;
                                        Log.Info("Changing keyboard back to Simplified Conversation Alpha.");
                                        Action simplifiedConversationAlphaBackAction = () =>
                                        {
                                            Log.Info("Restoring window size.");
                                            mainWindowManipulationService.Restore();
                                            Keyboard = new Menu(() => Keyboard = new Alpha1());
                                        };
                                        Keyboard = new SimplifiedConversationAlpha(simplifiedConversationAlphaBackAction);
                                        break;

                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.WebBrowsing":
                                        Settings.Default.UsingCommuniKateKeyboardLayout = Settings.Default.UseCommuniKateKeyboardLayoutByDefault;
                                        Log.Info("Changing keyboard back to Web Browsing.");
                                        Keyboard = new WebBrowsing();
                                        break;

                                    case "JuliusSweetland.OptiKey.UI.ViewModels.Keyboards.DJController":
                                        Settings.Default.UsingCommuniKateKeyboardLayout = Settings.Default.UseCommuniKateKeyboardLayoutByDefault;
                                        Log.Info("Changing keyboard back to DJ Controller.");
                                        Keyboard = new DJController();
                                        break;

                                    default:
                                        if (string.IsNullOrEmpty(Settings.Default.CommuniKateKeyboardCurrentContext))
                                        {
                                            Settings.Default.CommuniKateKeyboardPrevious1Context = Settings.Default.CommuniKateDefaultBoard;
                                            Settings.Default.CommuniKateKeyboardPrevious2Context = Settings.Default.CommuniKateDefaultBoard;
                                            Settings.Default.CommuniKateKeyboardPrevious3Context = Settings.Default.CommuniKateDefaultBoard;
                                            Settings.Default.CommuniKateKeyboardPrevious4Context = Settings.Default.CommuniKateDefaultBoard;
                                        }
                                        else if (Settings.Default.CommuniKateKeyboardPrevious1Context == board)
                                        {
                                            Settings.Default.CommuniKateKeyboardPrevious1Context = Settings.Default.CommuniKateKeyboardPrevious2Context;
                                            Settings.Default.CommuniKateKeyboardPrevious2Context = Settings.Default.CommuniKateKeyboardPrevious3Context;
                                            Settings.Default.CommuniKateKeyboardPrevious3Context = Settings.Default.CommuniKateKeyboardPrevious4Context;
                                            Settings.Default.CommuniKateKeyboardPrevious4Context = Settings.Default.CommuniKateDefaultBoard;
                                        }
                                        else
                                        {
                                            Settings.Default.CommuniKateKeyboardPrevious4Context = Settings.Default.CommuniKateKeyboardPrevious3Context;
                                            Settings.Default.CommuniKateKeyboardPrevious3Context = Settings.Default.CommuniKateKeyboardPrevious2Context;
                                            Settings.Default.CommuniKateKeyboardPrevious2Context = Settings.Default.CommuniKateKeyboardPrevious1Context;
                                            Settings.Default.CommuniKateKeyboardPrevious1Context = Settings.Default.CommuniKateKeyboardCurrentContext;
                                        }

                                        Settings.Default.CommuniKateKeyboardCurrentContext = board;
                                        Log.InfoFormat("CommuniKate keyboard page changed to {0}.", board);
                                        break;
                                }
                            }
                            else if (action.StartsWith("text:"))
                            {
                                keyboardOutputService.ProcessSingleKeyText(action.Substring(5));
                            }
                            else if (action.StartsWith("speak:"))
                            {
                                if (Settings.Default.CommuniKateSpeakSelected)
                                {
                                    var speechCommuniKate = audioService.SpeakNewOrInterruptCurrentSpeech(
                                        action.Substring(6),
                                        () => { KeyStateService.KeyDownStates[KeyValues.SpeakKey].Value = KeyDownStates.Up; },
                                        Settings.Default.CommuniKateSpeakSelectedVolume,
                                        Settings.Default.CommuniKateSpeakSelectedRate,
                                        Settings.Default.SpeechVoice);
                                    KeyStateService.KeyDownStates[KeyValues.SpeakKey].Value = speechCommuniKate ? KeyDownStates.Down : KeyDownStates.Up;
                                }
                            }
                            else if (action.StartsWith("sound:"))
                                audioService.PlaySound(action.Substring(6), Settings.Default.CommuniKateSoundVolume);
                            else if (action.StartsWith("action:"))
                            {
                                string thisAction = action.Substring(7);
                                if (thisAction.StartsWith("+"))
                                {
                                    bool changedAutoSpace = false;
                                    if (Settings.Default.AutoAddSpace)
                                    {
                                        Settings.Default.AutoAddSpace = false;
                                        changedAutoSpace = true;
                                    }
                                    foreach (char letter in thisAction.Substring(1))
                                        keyboardOutputService.ProcessSingleKeyText(letter.ToString());

                                    if (changedAutoSpace)
                                        Settings.Default.AutoAddSpace = true;
                                }
                                else if (thisAction.StartsWith(":"))
                                    switch (thisAction)
                                    {
                                        case ":space":
                                            keyboardOutputService.ProcessSingleKeyText(" ");
                                            break;
                                        case ":home":
                                            Settings.Default.CommuniKateKeyboardCurrentContext = Settings.Default.CommuniKateDefaultBoard;
                                            Log.InfoFormat("CommuniKate keyboard page changed to home board.");
                                            break;
                                        case ":speak":
                                            keyboardOutputService.ProcessFunctionKey(FunctionKeys.Speak);
                                            break;
                                        case ":clear":
                                            keyboardOutputService.ProcessFunctionKey(FunctionKeys.ClearScratchpad);
                                            break;
                                        case ":deleteword":
                                            keyboardOutputService.ProcessFunctionKey(FunctionKeys.BackMany);
                                            break;
                                        case ":backspace":
                                            keyboardOutputService.ProcessFunctionKey(FunctionKeys.BackOne);
                                            break;
                                        case ":ext_volume_up":
                                            Native.PInvoke.keybd_event((byte)Keys.VolumeUp, 0, 0, 0);
                                            break;
                                        case ":ext_volume_down":
                                            Native.PInvoke.keybd_event((byte)Keys.VolumeDown, 0, 0, 0);
                                            break;
                                        case ":ext_volume_mute":
                                            Native.PInvoke.keybd_event((byte)Keys.VolumeMute, 0, 0, 0);
                                            break;
                                        case ":ext_media_next":
                                            Native.PInvoke.keybd_event((byte)Keys.MediaNextTrack, 0, 0, 0);
                                            break;
                                        case ":ext_media_previous":
                                            Native.PInvoke.keybd_event((byte)Keys.MediaPreviousTrack, 0, 0, 0);
                                            break;
                                        case ":ext_media_pause":
                                            Native.PInvoke.keybd_event((byte)Keys.MediaPlayPause, 0, 0, 0);
                                            break;
                                        case ":ext_letters":
                                            Settings.Default.UsingCommuniKateKeyboardLayout = false;
                                            if (mainWindowManipulationService.WindowState == WindowStates.Maximised)
                                            {
                                                Log.Info("Changing keyboard to ConversationAlpha.");
                                                Action conversationAlphaBackAction = () =>
                                                {
                                                    Settings.Default.UsingCommuniKateKeyboardLayout = true;
                                                    Keyboard = currentKeyboard;
                                                };
                                                Keyboard = new ConversationAlpha1(conversationAlphaBackAction);
                                            }
                                            else
                                            {
                                                Log.Info("Changing keyboard to Alpha.");
                                                Keyboard = new Alpha1();
                                            }
                                            break;

                                        case ":ext_numbers":
                                            if (mainWindowManipulationService.WindowState == WindowStates.Maximised)
                                            {
                                                Log.Info("Changing keyboard to ConversationNumericAndSymbols.");
                                                Action BackAction = () =>
                                                {
                                                    Keyboard = currentKeyboard;
                                                };
                                                Keyboard = new ConversationNumericAndSymbols(BackAction);
                                            }
                                            else
                                            {
                                                Log.Info("Changing keyboard to Numeric And Symbols.");
                                                Keyboard = new NumericAndSymbols1();
                                            }
                                            break;

                                        case ":ext_mouse":
                                            if (mainWindowManipulationService.WindowState != WindowStates.Maximised)
                                            {
                                                Log.Info("Changing keyboard to Mouse.");
                                                Action BackAction = () =>
                                                {
                                                    Keyboard = currentKeyboard;
                                                };
                                                Keyboard = new Mouse(BackAction);
                                            }
                                            else
                                            {
                                                Log.Info("Changing keyboard to Mouse.");
                                                Action BackAction = () =>
                                                {
                                                    Keyboard = currentKeyboard;
                                                    Log.Info("Maximising window.");
                                                    mainWindowManipulationService.Maximise();
                                                };
                                                Keyboard = new Mouse(BackAction);
                                                Log.Info("Restoring window size.");
                                                mainWindowManipulationService.Restore();
                                            }
                                            break;
                                        default:
                                            Log.InfoFormat("Unsupported CommuniKate action: {0}.", thisAction);
                                            break;
                                    }
                                else
                                    Log.InfoFormat("Unsupported CommuniKate action: {0}.", thisAction);
                            }

                        }
                    }
                    
                    break;

                case FunctionKeys.SelectVoice:
                    SelectVoice(singleKeyValue.String);
                    break;

                case FunctionKeys.MIDIMessage:
                    SendMIDIMessage(singleKeyValue.String);
                    break;
            }
        }

        private void HandleFunctionKeySelectionResult(KeyValue singleKeyValue)
        {
            var currentKeyboard = Keyboard;
            Action resumeLookToScroll;

            switch (singleKeyValue.FunctionKey.Value)
            {
                case FunctionKeys.AddToDictionary:
                    AddTextToDictionary();
                    break;

                case FunctionKeys.Alpha1Keyboard:
                    if (Settings.Default.EnableCommuniKateKeyboardLayout)
                    {
                        Settings.Default.UsingCommuniKateKeyboardLayout = Settings.Default.UseCommuniKateKeyboardLayoutByDefault;
                        Settings.Default.CommuniKateKeyboardCurrentContext = Settings.Default.CommuniKateDefaultBoard;
                        Settings.Default.CommuniKateKeyboardPrevious1Context = currentKeyboard.ToString();
                    }
                    Log.Info("Changing keyboard to Alpha1.");
                    Keyboard = new Alpha1();
                    break;

                case FunctionKeys.Alpha2Keyboard:
                    Log.Info("Changing keyboard to Alpha2.");
                    Keyboard = new Alpha2();
                    break;

                case FunctionKeys.Attention:
                    audioService.PlaySound(Settings.Default.AttentionSoundFile, 
                        Settings.Default.AttentionSoundVolume);
                    break;

                case FunctionKeys.BackFromKeyboard:
                    Log.Info("Navigating back from keyboard.");
                    var navigableKeyboard = Keyboard as IBackAction;
                    if (navigableKeyboard != null && navigableKeyboard.BackAction != null)
                    {
                        navigableKeyboard.BackAction();
                    }
                    else
                    {
                        Log.Error("Keyboard doesn't have back action, going back to initial keyboard instead");
                        Keyboard = new Alpha1();
                        if (Settings.Default.EnableCommuniKateKeyboardLayout)
                        {
                            Settings.Default.UsingCommuniKateKeyboardLayout = Settings.Default.UseCommuniKateKeyboardLayoutByDefault;
                            Settings.Default.CommuniKateKeyboardCurrentContext = Settings.Default.CommuniKateDefaultBoard;
                            Settings.Default.CommuniKateKeyboardPrevious1Context = currentKeyboard.ToString();
                        }
                      
                        InitialiseKeyboard(this.mainWindowManipulationService);                     
                    }
                    break;

                case FunctionKeys.Calibrate:
                    if (CalibrationService != null)
                    {
                        Log.Info("Calibrate requested.");
                            
                        var question = CalibrationService.CanBeCompletedWithoutManualIntervention
                            ? Resources.CALIBRATION_CONFIRMATION_MESSAGE
                            : Resources.CALIBRATION_REQUIRES_MANUAL_INTERACTION;
                            
                        Keyboard = new YesNoQuestion(
                            question,
                            () =>
                            {
                                inputService.RequestSuspend();
                                CalibrateRequest.Raise(new NotificationWithCalibrationResult(), calibrationResult =>
                                {
                                    if (calibrationResult.Success)
                                    {
                                        audioService.PlaySound(Settings.Default.InfoSoundFile, Settings.Default.InfoSoundVolume);
                                        RaiseToastNotification(Resources.SUCCESS, calibrationResult.Message, NotificationTypes.Normal, () => inputService.RequestResume());
                                    }
                                    else
                                    {
                                        audioService.PlaySound(Settings.Default.ErrorSoundFile, Settings.Default.ErrorSoundVolume);
                                        RaiseToastNotification(Resources.CRASH_TITLE, calibrationResult.Exception != null
                                                ? calibrationResult.Exception.Message
                                                : calibrationResult.Message ?? Resources.UNKNOWN_CALIBRATION_ERROR, 
                                            NotificationTypes.Error, 
                                            () => inputService.RequestResume());
                                    }
                                });
                                Keyboard = currentKeyboard;
                            },
                            () =>
                            {
                                Keyboard = currentKeyboard;
                            });
                    }
                    break;

                case FunctionKeys.CatalanSpain:
                    SelectLanguage(Languages.CatalanSpain);
                    break;

                case FunctionKeys.CollapseDock:
                    Log.Info("Collapsing dock.");
                    mainWindowManipulationService.ResizeDockToCollapsed();
                    if (Keyboard is ViewModels.Keyboards.Mouse)
                    {
                        Settings.Default.MouseKeyboardDockSize = DockSizes.Collapsed;
                    }
                    break;

                case FunctionKeys.CommuniKateKeyboard:
                    Settings.Default.CommuniKateKeyboardCurrentContext = Settings.Default.CommuniKateDefaultBoard;
                    Settings.Default.UsingCommuniKateKeyboardLayout = true;
                    Settings.Default.CommuniKateKeyboardPrevious1Context = currentKeyboard.ToString();
                    Log.Info("Changing keyboard to CommuniKate.");
                    Keyboard = new Alpha1();
                    break;

                case FunctionKeys.ConversationAlpha1Keyboard:
                    if (Settings.Default.EnableCommuniKateKeyboardLayout)
                    {
                        Settings.Default.UsingCommuniKateKeyboardLayout = Settings.Default.UseCommuniKateKeyboardLayoutByDefault;
                        Settings.Default.CommuniKateKeyboardCurrentContext = Settings.Default.CommuniKateDefaultBoard;
                        Settings.Default.CommuniKateKeyboardPrevious1Context = currentKeyboard.ToString();
                    }
                    Log.Info("Changing keyboard to ConversationAlpha1.");
                    var opacityBeforeConversationAlpha1 = mainWindowManipulationService.GetOpacity();
                    Action conversationAlpha1BackAction = currentKeyboard is ConversationAlpha2
                        ? ((ConversationAlpha2)currentKeyboard).BackAction
                        : currentKeyboard is ConversationNumericAndSymbols
                            ? ((ConversationNumericAndSymbols)currentKeyboard).BackAction
                            : currentKeyboard is SimplifiedConversationAlpha
                                ? ((SimplifiedConversationAlpha)currentKeyboard).BackAction
                                : currentKeyboard is ConversationConfirm
                                    ? ((ConversationConfirm)currentKeyboard).BackAction
                                    : () =>
                                    {
                                        Log.Info("Restoring window size.");
                                        mainWindowManipulationService.Restore();
                                        Log.InfoFormat("Restoring window opacity to {0}", opacityBeforeConversationAlpha1);
                                        mainWindowManipulationService.SetOpacity(opacityBeforeConversationAlpha1);
                                        Keyboard = currentKeyboard;
                                    };
                    Keyboard = new ConversationAlpha1(conversationAlpha1BackAction);
                    Log.Info("Maximising window.");
                    mainWindowManipulationService.Maximise();
                    Log.InfoFormat("Setting opacity to 1 (fully opaque)");
                    mainWindowManipulationService.SetOpacity(1);
                    break;

                case FunctionKeys.ConversationAlpha2Keyboard:
                    Log.Info("Changing keyboard to ConversationAlpha2.");
                    var opacityBeforeConversationAlpha2 = mainWindowManipulationService.GetOpacity();
                    Action conversationAlpha2BackAction = currentKeyboard is ConversationAlpha1
                        ? ((ConversationAlpha1)currentKeyboard).BackAction
                        : currentKeyboard is ConversationNumericAndSymbols
                            ? ((ConversationNumericAndSymbols)currentKeyboard).BackAction
                            : currentKeyboard is SimplifiedConversationAlpha
                                ? ((SimplifiedConversationAlpha)currentKeyboard).BackAction
                                : currentKeyboard is ConversationConfirm
                                    ? ((ConversationConfirm)currentKeyboard).BackAction
                                    : () =>
                                    {
                                        Log.Info("Restoring window size.");
                                        mainWindowManipulationService.Restore();
                                        Log.InfoFormat("Restoring window opacity to {0}", opacityBeforeConversationAlpha2);
                                        mainWindowManipulationService.SetOpacity(opacityBeforeConversationAlpha2);
                                        Keyboard = currentKeyboard;
                                    };
                    Keyboard = new ConversationAlpha2(conversationAlpha2BackAction);
                    Log.Info("Maximising window.");
                    mainWindowManipulationService.Maximise();
                    Log.InfoFormat("Setting opacity to 1 (fully opaque)");
                    mainWindowManipulationService.SetOpacity(1);
                    break;

                case FunctionKeys.ConversationCommuniKateKeyboard:
                    Settings.Default.CommuniKateKeyboardCurrentContext = Settings.Default.CommuniKateDefaultBoard;
                    Settings.Default.UsingCommuniKateKeyboardLayout = true;
                    Settings.Default.CommuniKateKeyboardPrevious1Context = currentKeyboard.ToString();
                    Log.Info("Changing keyboard to Conversation CommuniKate.");
                    Action conversationAlphaBackAction = () =>
                    {
                        Log.Info("Restoring window size.");
                        mainWindowManipulationService.Restore();
                        Keyboard = new Menu(() => Keyboard = new Alpha1());
                    };
                    Keyboard = new ConversationAlpha1(conversationAlphaBackAction);
                    break;

                case FunctionKeys.ConversationConfirmKeyboard:
                    Log.Info("Changing keyboard to ConversationConfirm.");
                    var opacityBeforeConversationConfirm = mainWindowManipulationService.GetOpacity();
                    Action conversationConfirmBackAction = currentKeyboard is ConversationAlpha1
                        ? ((ConversationAlpha1)currentKeyboard).BackAction
                        : currentKeyboard is ConversationAlpha2
                            ? ((ConversationAlpha2)currentKeyboard).BackAction
                            : currentKeyboard is SimplifiedConversationAlpha
                                ? ((SimplifiedConversationAlpha)currentKeyboard).BackAction
                                : currentKeyboard is ConversationNumericAndSymbols
                                    ? ((ConversationNumericAndSymbols)currentKeyboard).BackAction
                                    : () =>
                                    {
                                        Log.Info("Restoring window size.");
                                        mainWindowManipulationService.Restore();
                                        Log.InfoFormat("Restoring window opacity to {0}", opacityBeforeConversationConfirm);
                                        mainWindowManipulationService.SetOpacity(opacityBeforeConversationConfirm);
                                        Keyboard = currentKeyboard;
                                    };
                    Keyboard = new ConversationConfirm(conversationConfirmBackAction);
                    Log.Info("Maximising window.");
                    mainWindowManipulationService.Maximise();
                    Log.InfoFormat("Setting opacity to 1 (fully opaque)");
                    mainWindowManipulationService.SetOpacity(1);
                    break;

                case FunctionKeys.ConversationNumericAndSymbolsKeyboard:
                    Log.Info("Changing keyboard to ConversationNumericAndSymbols.");
                    var opacityBeforeConversationNumericAndSymbols = mainWindowManipulationService.GetOpacity();
                    Action conversationNumericAndSymbolsBackAction = currentKeyboard is ConversationConfirm
                        ? ((ConversationConfirm)currentKeyboard).BackAction
                        : currentKeyboard is ConversationAlpha1
                            ? ((ConversationAlpha1)currentKeyboard).BackAction
                            : currentKeyboard is ConversationAlpha2
                                ? ((ConversationAlpha2)currentKeyboard).BackAction
                                : currentKeyboard is SimplifiedConversationAlpha
                                    ? ((SimplifiedConversationAlpha)currentKeyboard).BackAction
                                    : () =>
                                    {
                                        Log.Info("Restoring window size.");
                                        mainWindowManipulationService.Restore();
                                        Log.InfoFormat("Restoring window opacity to {0}", opacityBeforeConversationNumericAndSymbols);
                                        mainWindowManipulationService.SetOpacity(opacityBeforeConversationNumericAndSymbols);
                                        Keyboard = currentKeyboard;
                                    };
                    Keyboard = new ConversationNumericAndSymbols(conversationNumericAndSymbolsBackAction);
                    Log.Info("Maximising window.");
                    mainWindowManipulationService.Maximise();
                    Log.InfoFormat("Setting opacity to 1 (fully opaque)");
                    mainWindowManipulationService.SetOpacity(1);
                    break;

                case FunctionKeys.CroatianCroatia:
                    SelectLanguage(Languages.CroatianCroatia);
                    break;

                case FunctionKeys.Currencies1Keyboard:
                    Log.Info("Changing keyboard to Currencies1.");
                    Keyboard = new Currencies1();
                    break;

                case FunctionKeys.Currencies2Keyboard:
                    Log.Info("Changing keyboard to Currencies2.");
                    Keyboard = new Currencies2();
                    break;

                case FunctionKeys.DynamicKeyboard:
                    {
                        Log.Info("Changing keyboard to DynamicKeyboard.");

                        var currentKeyboard2 = Keyboard;

                        Action reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                        Action backAction = () =>
                        {
                            Keyboard = currentKeyboard2;

                            reinstateModifiers();

                            // Clear the scratchpad when leaving keyboard
                            // (proper scratchpad functionality not supported in dynamic keyboards presently
                            keyboardOutputService.ProcessFunctionKey(FunctionKeys.ClearScratchpad);
                        };

                        int pageIndex = 0;
                        if (Keyboard is DynamicKeyboardSelector)
                        {
                            var kb = Keyboard as DynamicKeyboardSelector;
                            backAction = kb.BackAction;
                            pageIndex = kb.PageIndex + 1;
                        }
                        Keyboard = new DynamicKeyboardSelector(backAction, pageIndex);
                    }
                    break;


                case FunctionKeys.DynamicKeyboardPrev:
                    {
                        Log.Info("Changing keyboard to prev DynamicKeyboard.");

                        Action backAction;
                        var currentKeyboard2 = Keyboard;
                        int pageIndex = 0;
                        if (Keyboard is DynamicKeyboardSelector)
                        {
                            var kb = Keyboard as DynamicKeyboardSelector;
                            backAction = kb.BackAction;
                            pageIndex = kb.PageIndex - 1;
                        }
                        else
                        {
                            Log.Error("Unexpectedly entering DynamicKeyboardPrev from somewhere other than DynamicKeyboard");
                            backAction = () =>
                            {
                                Keyboard = currentKeyboard2;
                            };
                        }
                        Keyboard = new DynamicKeyboardSelector(backAction, pageIndex);
                    }
                    break;

            case FunctionKeys.DynamicKeyboardNext:
                {
                    Log.Info("Changing keyboard to next DynamicKeyboard.");

                    Action backAction;
                    var currentKeyboard2 = Keyboard;
                    int pageIndex = 0;
                    if (Keyboard is DynamicKeyboardSelector)
                    {
                        var kb = Keyboard as DynamicKeyboardSelector;
                        backAction = kb.BackAction;
                        pageIndex = kb.PageIndex + 1;
                    }
                    else
                    {
                        Log.Error("Unexpectedly entering DynamicKeyboardNext from somewhere other than DynamicKeyboard");
                        backAction = () =>
                        {
                            Keyboard = currentKeyboard2;
                        };
                    }
                    Keyboard = new DynamicKeyboardSelector(backAction, pageIndex);
                }
                break;

                case FunctionKeys.CzechCzechRepublic:
                    SelectLanguage(Languages.CzechCzechRepublic);
                    break;

                case FunctionKeys.DanishDenmark:
                    SelectLanguage(Languages.DanishDenmark);
                    break;

                case FunctionKeys.DecreaseOpacity:
                    Log.Info("Decreasing opacity.");
                    mainWindowManipulationService.IncrementOrDecrementOpacity(false);
                    break;

                case FunctionKeys.Diacritic1Keyboard:
                    Log.Info("Changing keyboard to Diacritic1.");
                    Keyboard = new Diacritics1();
                    break;

                case FunctionKeys.Diacritic2Keyboard:
                    Log.Info("Changing keyboard to Diacritic2.");
                    Keyboard = new Diacritics2();
                    break;

                case FunctionKeys.Diacritic3Keyboard:
                    Log.Info("Changing keyboard to Diacritic3.");
                    Keyboard = new Diacritics3();
                    break;

                case FunctionKeys.DutchBelgium:
                    SelectLanguage(Languages.DutchBelgium);
                    break;

                case FunctionKeys.DutchNetherlands:
                    SelectLanguage(Languages.DutchNetherlands);
                    break;

                case FunctionKeys.EnglishCanada:
                    SelectLanguage(Languages.EnglishCanada);
                    break;

                case FunctionKeys.EnglishUK:
                    SelectLanguage(Languages.EnglishUK);
                    break;

                case FunctionKeys.EnglishUS:
                    SelectLanguage(Languages.EnglishUS);
                    break;

                case FunctionKeys.ExpandDock:
                    Log.Info("Expanding dock.");
                    mainWindowManipulationService.ResizeDockToFull();
                    if (Keyboard is ViewModels.Keyboards.Mouse)
                    {
                        Settings.Default.MouseKeyboardDockSize = DockSizes.Full;
                    }
                    break;

                case FunctionKeys.ExpandToBottom:
                    Log.InfoFormat("Expanding to bottom by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Expand(ExpandToDirections.Bottom, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.ExpandToBottomAndLeft:
                    Log.InfoFormat("Expanding to bottom and left by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Expand(ExpandToDirections.BottomLeft, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.ExpandToBottomAndRight:
                    Log.InfoFormat("Expanding to bottom and right by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Expand(ExpandToDirections.BottomRight, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.ExpandToLeft:
                    Log.InfoFormat("Expanding to left by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Expand(ExpandToDirections.Left, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.ExpandToRight:
                    Log.InfoFormat("Expanding to right by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Expand(ExpandToDirections.Right, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.ExpandToTop:
                    Log.InfoFormat("Expanding to top by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Expand(ExpandToDirections.Top, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.ExpandToTopAndLeft:
                    Log.InfoFormat("Expanding to top and left by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Expand(ExpandToDirections.TopLeft, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.ExpandToTopAndRight:
                    Log.InfoFormat("Expanding to top and right by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Expand(ExpandToDirections.TopRight, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.FrenchCanada:
                    SelectLanguage(Languages.FrenchCanada);
                    break;

                case FunctionKeys.FrenchFrance:
                    SelectLanguage(Languages.FrenchFrance);
                    break;

                case FunctionKeys.GermanGermany:
                    SelectLanguage(Languages.GermanGermany);
                    break;

                case FunctionKeys.GreekGreece:
                    SelectLanguage(Languages.GreekGreece);
                    break;

                case FunctionKeys.IncreaseOpacity:
                    Log.Info("Increasing opacity.");
                    mainWindowManipulationService.IncrementOrDecrementOpacity(true);
                    break;

                case FunctionKeys.ItalianItaly:
                    SelectLanguage(Languages.ItalianItaly);
                    break;

                case FunctionKeys.JapaneseJapan:
                    SelectLanguage(Languages.JapaneseJapan);
                    break;

                case FunctionKeys.KoreanKorea:
                    SelectLanguage(Languages.KoreanKorea);
                    break;

                case FunctionKeys.LanguageKeyboard:
                    Log.Info("Restoring window size.");
                    mainWindowManipulationService.Restore();
                    Log.Info("Changing keyboard to Language.");
                    Keyboard = new Language(() => Keyboard = currentKeyboard);
                    break;

                case FunctionKeys.LookToScrollActive:
                    ToggleLookToScroll();
                    break;

                case FunctionKeys.LookToScrollBounds:
                    HandleLookToScrollBoundsKeySelected();
                    break;

                case FunctionKeys.LookToScrollIncrement:
                    SelectNextLookToScrollIncrement();
                    break;

                case FunctionKeys.LookToScrollMode:
                    SelectNextLookToScrollMode();
                    break;

                case FunctionKeys.LookToScrollSpeed:
                    SelectNextLookToScrollSpeed();
                    break;

                case FunctionKeys.MenuKeyboard:
                    Log.Info("Restoring window size.");
                    mainWindowManipulationService.Restore();
                    Log.Info("Changing keyboard to Menu.");
                    Keyboard = new Menu(() => Keyboard = currentKeyboard);
                    break;

                case FunctionKeys.Minimise:
                    Log.Info("Minimising window.");
                    mainWindowManipulationService.Minimise();
                    Log.Info("Changing keyboard to Minimised.");
                    Keyboard = new Minimised(() =>
                    {
                        Log.Info("Restoring window size.");
                        mainWindowManipulationService.Restore();
                        Keyboard = currentKeyboard;
                    });
                    break;

                case FunctionKeys.More:
                    ShowMore();
                    break;

                case FunctionKeys.MouseDrag:
                    Log.Info("Mouse drag selected.");
                    resumeLookToScroll = SuspendLookToScrollWhileChoosingPointForMouse();
                    SetupFinalClickAction(firstFinalPoint =>
                    {
                        if (firstFinalPoint != null)
                        {
                            audioService.PlaySound(Settings.Default.MouseDownSoundFile, Settings.Default.MouseDownSoundVolume);
                                
                            //This class reacts to the point selection event AFTER the MagnifyPopup reacts to it.
                            //This means that if the MagnifyPopup sets the nextPointSelectionAction from the
                            //MagnifiedPointSelectionAction then it will be called immediately i.e. for the same point.
                            //The workaround is to set the nextPointSelectionAction to a lambda which sets the NEXT
                            //nextPointSelectionAction. This means the immediate call to the lambda just sets up the
                            //delegate for the subsequent call.
                            nextPointSelectionAction = repeatFirstClickOrSecondClickAction =>
                            {
                                Action<Point> deferIfMagnifyingElseDoNow = repeatFirstClickOrSecondClickPoint =>
                                {
                                    Action<Point?> secondFinalClickAction = secondFinalPoint =>
                                    {
                                        if (secondFinalPoint != null)
                                        {
                                            Action<Point, Point> simulateDrag = (fp1, fp2) =>
                                            {
                                                Log.InfoFormat("Performing mouse drag between points ({0},{1}) and {2},{3}).", fp1.X, fp1.Y, fp2.X, fp2.Y);
                                                Action reinstateModifiers = () => { };
                                                if (keyStateService.SimulateKeyStrokes
                                                    && Settings.Default.SuppressModifierKeysForAllMouseActions)
                                                {
                                                    reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                                                }
                                                mouseOutputService.MoveTo(fp1);
                                                audioService.PlaySound(Settings.Default.MouseDownSoundFile, Settings.Default.MouseDownSoundVolume);
                                                mouseOutputService.LeftButtonDown();
                                                Thread.Sleep(Settings.Default.MouseDragDelayAfterLeftMouseButtonDownBeforeMove);

                                                Vector stepVector = fp1 - fp2;
                                                int steps = Settings.Default.MouseDragNumberOfSteps; 
                                                stepVector = stepVector / steps;

                                                do
                                                {
                                                    fp1.X = fp1.X - stepVector.X;
                                                    fp1.Y = fp1.Y - stepVector.Y;
                                                    mouseOutputService.MoveTo(fp1);
                                                    Thread.Sleep(Settings.Default.MouseDragDelayBetweenEachStep);
                                                    steps--;
                                                } while (steps > 0);
                                                
                                                mouseOutputService.MoveTo(fp2);
                                                Thread.Sleep(Settings.Default.MouseDragDelayAfterMoveBeforeLeftMouseButtonUp);
                                                audioService.PlaySound(Settings.Default.MouseUpSoundFile, Settings.Default.MouseUpSoundVolume);
                                                mouseOutputService.LeftButtonUp();
                                                reinstateModifiers();
                                            };

                                            lastMouseActionStateManager.LastMouseAction =
                                                () => simulateDrag(firstFinalPoint.Value, secondFinalPoint.Value);
                                            simulateDrag(firstFinalPoint.Value, secondFinalPoint.Value);
                                        }

                                        ResetAndCleanupAfterMouseAction();
                                        resumeLookToScroll();
                                    };

                                    if (keyStateService.KeyDownStates[KeyValues.MouseMagnifierKey].Value.IsDownOrLockedDown())
                                    {
                                        ShowCursor = false; //See MouseMoveAndLeftClick case for explanation of this
                                        MagnifiedPointSelectionAction = secondFinalClickAction;
                                        MagnifyAtPoint = repeatFirstClickOrSecondClickPoint;
                                        ShowCursor = true;
                                    }
                                    else
                                    {
                                        secondFinalClickAction(repeatFirstClickOrSecondClickPoint);
                                    }

                                    nextPointSelectionAction = null;
                                };

                                if (keyStateService.KeyDownStates[KeyValues.MouseMagnifierKey].Value.IsDownOrLockedDown())
                                {
                                    nextPointSelectionAction = deferIfMagnifyingElseDoNow;
                                }
                                else
                                {
                                    deferIfMagnifyingElseDoNow(repeatFirstClickOrSecondClickAction);
                                }
                            };
                        }
                        else
                        {
                            //Reset and clean up if we are not continuing to 2nd point
                            SelectionMode = SelectionModes.Key;
                            nextPointSelectionAction = null;
                            ShowCursor = false;
                            if (keyStateService.KeyDownStates[KeyValues.MouseMagnifierKey].Value == KeyDownStates.Down)
                            {
                                keyStateService.KeyDownStates[KeyValues.MouseMagnifierKey].Value = KeyDownStates.Up; //Release magnifier if down but not locked down
                            }
                            resumeLookToScroll();
                        }

                        //Reset and clean up
                        MagnifyAtPoint = null;
                        MagnifiedPointSelectionAction = null;
                    }, finalClickInSeries: false);
                    break;

                case FunctionKeys.MouseKeyboard:
                    {
                        Log.Info("Changing keyboard to Mouse.");
                        Action backAction;
                        if (keyStateService.SimulateKeyStrokes
                            && Settings.Default.SuppressModifierKeysWhenInMouseKeyboard)
                        {
                            var restoreModifierStates = keyStateService.ReleaseModifiers(Log);
                            backAction = () =>
                            {
                                restoreModifierStates();
                                Keyboard = currentKeyboard;
                            };
                        }
                        else
                        {
                            backAction = () => Keyboard = currentKeyboard;
                        }
                        Keyboard = new Mouse(backAction);
                        //Reinstate mouse keyboard docked state (if docked)
                        if (Settings.Default.MainWindowState == WindowStates.Docked)
                        {
                            if (Settings.Default.MouseKeyboardDockSize == DockSizes.Full
                                && Settings.Default.MainWindowDockSize != DockSizes.Full)
                            {
                                mainWindowManipulationService.ResizeDockToFull();
                            }
                            else if (Settings.Default.MouseKeyboardDockSize == DockSizes.Collapsed
                                && Settings.Default.MainWindowDockSize != DockSizes.Collapsed)
                            {
                                mainWindowManipulationService.ResizeDockToCollapsed();
                            }
                        }
                    }
                    break;

                case FunctionKeys.MouseLeftClick:
                    var leftClickPoint = mouseOutputService.GetCursorPosition();
                    Log.InfoFormat("Mouse left click selected at point ({0},{1}).", leftClickPoint.X, leftClickPoint.Y);
                    Action performLeftClick = () =>
                    {
                        Action reinstateModifiers = () => { };
                        if (keyStateService.SimulateKeyStrokes
                            && Settings.Default.SuppressModifierKeysForAllMouseActions)
                        {
                            reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                        }
                        mouseOutputService.MoveTo(leftClickPoint);
                        audioService.PlaySound(Settings.Default.MouseClickSoundFile, Settings.Default.MouseClickSoundVolume);
                        mouseOutputService.LeftButtonClick();
                        reinstateModifiers();
                    };
                    lastMouseActionStateManager.LastMouseAction = () => performLeftClick();
                    performLeftClick();
                    break;

                case FunctionKeys.MouseLeftDoubleClick:
                    var leftDoubleClickPoint = mouseOutputService.GetCursorPosition();
                    Log.InfoFormat("Mouse left double click selected at point ({0},{1}).", leftDoubleClickPoint.X, leftDoubleClickPoint.Y);
                    Action performLeftDoubleClick = () =>
                    {
                        Action reinstateModifiers = () => { };
                        if (keyStateService.SimulateKeyStrokes
                            && Settings.Default.SuppressModifierKeysForAllMouseActions)
                        {
                            reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                        }
                        mouseOutputService.MoveTo(leftDoubleClickPoint);
                        audioService.PlaySound(Settings.Default.MouseDoubleClickSoundFile, Settings.Default.MouseDoubleClickSoundVolume);
                        mouseOutputService.LeftButtonDoubleClick();
                        reinstateModifiers();
                    };
                    lastMouseActionStateManager.LastMouseAction = () => performLeftDoubleClick();
                    performLeftDoubleClick();
                    break;

                case FunctionKeys.MouseLeftDownUp:
                    var leftDownUpPoint = mouseOutputService.GetCursorPosition();
                    if (keyStateService.KeyDownStates[KeyValues.MouseLeftDownUpKey].Value.IsDownOrLockedDown())
                    {
                        Log.InfoFormat("Pressing mouse left button down at point ({0},{1}).", leftDownUpPoint.X, leftDownUpPoint.Y);
                        Action reinstateModifiers = () => { };
                        if (keyStateService.SimulateKeyStrokes
                            && Settings.Default.SuppressModifierKeysForAllMouseActions)
                        {
                            reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                        }
                        audioService.PlaySound(Settings.Default.MouseDownSoundFile, Settings.Default.MouseDownSoundVolume);
                        mouseOutputService.LeftButtonDown();
                        reinstateModifiers();
                        lastMouseActionStateManager.LastMouseAction = null;
                    }
                    else
                    {
                        Log.InfoFormat("Releasing mouse left button at point ({0},{1}).", leftDownUpPoint.X, leftDownUpPoint.Y);
                        Action reinstateModifiers = () => { };
                        if (keyStateService.SimulateKeyStrokes
                            && Settings.Default.SuppressModifierKeysForAllMouseActions)
                        {
                            reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                        }
                        audioService.PlaySound(Settings.Default.MouseUpSoundFile, Settings.Default.MouseUpSoundVolume);
                        mouseOutputService.LeftButtonUp();
                        reinstateModifiers();
                        lastMouseActionStateManager.LastMouseAction = null;
                    }
                    break;

                case FunctionKeys.MouseMiddleClick:
                    var middleClickPoint = mouseOutputService.GetCursorPosition();
                    Log.InfoFormat("Mouse middle click selected at point ({0},{1}).", middleClickPoint.X, middleClickPoint.Y);
                    Action performMiddleClick = () =>
                    {
                        Action reinstateModifiers = () => { };
                        if (keyStateService.SimulateKeyStrokes
                            && Settings.Default.SuppressModifierKeysForAllMouseActions)
                        {
                            reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                        }
                        mouseOutputService.MoveTo(middleClickPoint);
                        audioService.PlaySound(Settings.Default.MouseClickSoundFile, Settings.Default.MouseClickSoundVolume);
                        mouseOutputService.MiddleButtonClick();
                        reinstateModifiers();
                    };
                    lastMouseActionStateManager.LastMouseAction = () => performMiddleClick();
                    performMiddleClick();
                    break;

                case FunctionKeys.MouseMiddleDownUp:
                    var middleDownUpPoint = mouseOutputService.GetCursorPosition();
                    if (keyStateService.KeyDownStates[KeyValues.MouseMiddleDownUpKey].Value.IsDownOrLockedDown())
                    {
                        Log.InfoFormat("Pressing mouse middle button down at point ({0},{1}).", middleDownUpPoint.X, middleDownUpPoint.Y);
                        Action reinstateModifiers = () => { };
                        if (keyStateService.SimulateKeyStrokes
                            && Settings.Default.SuppressModifierKeysForAllMouseActions)
                        {
                            reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                        }
                        audioService.PlaySound(Settings.Default.MouseDownSoundFile, Settings.Default.MouseDownSoundVolume);
                        mouseOutputService.MiddleButtonDown();
                        reinstateModifiers();
                        lastMouseActionStateManager.LastMouseAction = null;
                    }
                    else
                    {
                        Log.InfoFormat("Releasing mouse middle button at point ({0},{1}).", middleDownUpPoint.X, middleDownUpPoint.Y);
                        Action reinstateModifiers = () => { };
                        if (keyStateService.SimulateKeyStrokes
                            && Settings.Default.SuppressModifierKeysForAllMouseActions)
                        {
                            reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                        }
                        audioService.PlaySound(Settings.Default.MouseUpSoundFile, Settings.Default.MouseUpSoundVolume);
                        mouseOutputService.MiddleButtonUp();
                        reinstateModifiers();
                        lastMouseActionStateManager.LastMouseAction = null;
                    }
                    break;

                case FunctionKeys.MouseMoveAndLeftClick:
                    Log.Info("Mouse move and left click selected.");
                    resumeLookToScroll = SuspendLookToScrollWhileChoosingPointForMouse();
                    SetupFinalClickAction(finalPoint =>
                    {
                        if (finalPoint != null)
                        {
                            Action<Point> simulateClick = fp =>
                            {
                                Log.InfoFormat("Performing mouse left click at point ({0},{1}).", fp.X, fp.Y);
                                Action reinstateModifiers = () => { };
                                if (keyStateService.SimulateKeyStrokes
                                    && Settings.Default.SuppressModifierKeysForAllMouseActions)
                                {
                                    reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                                }
                                audioService.PlaySound(Settings.Default.MouseClickSoundFile, Settings.Default.MouseClickSoundVolume);
                                mouseOutputService.MoveAndLeftClick(fp, true);
                                reinstateModifiers();
                            };
                            lastMouseActionStateManager.LastMouseAction = () => simulateClick(finalPoint.Value);
                            ShowCursor = false; //Hide cursor popup before performing action as it is possible for it to be performed on the popup
                            simulateClick(finalPoint.Value);
                        }

                        ResetAndCleanupAfterMouseAction();
                        resumeLookToScroll();
                    });
                    break;

                case FunctionKeys.MouseMoveAndLeftDoubleClick:
                    Log.Info("Mouse move and left double click selected.");
                    resumeLookToScroll = SuspendLookToScrollWhileChoosingPointForMouse();
                    SetupFinalClickAction(finalPoint =>
                    {
                        if (finalPoint != null)
                        {
                            Action<Point> simulateClick = fp =>
                            {
                                Log.InfoFormat("Performing mouse left double click at point ({0},{1}).", fp.X, fp.Y);
                                Action reinstateModifiers = () => { };
                                if (keyStateService.SimulateKeyStrokes
                                    && Settings.Default.SuppressModifierKeysForAllMouseActions)
                                {
                                    reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                                }
                                audioService.PlaySound(Settings.Default.MouseDoubleClickSoundFile, Settings.Default.MouseDoubleClickSoundVolume);
                                mouseOutputService.MoveAndLeftDoubleClick(fp, true);
                                reinstateModifiers();
                            };
                            lastMouseActionStateManager.LastMouseAction = () => simulateClick(finalPoint.Value);
                            ShowCursor = false; //Hide cursor popup before performing action as it is possible for it to be performed on the popup
                            simulateClick(finalPoint.Value);
                        }
                            
                        ResetAndCleanupAfterMouseAction();
                        resumeLookToScroll();
                    });
                    break;

                case FunctionKeys.MouseMoveAndMiddleClick:
                    Log.Info("Mouse move and middle click selected.");
                    resumeLookToScroll = SuspendLookToScrollWhileChoosingPointForMouse();
                    SetupFinalClickAction(finalPoint =>
                    {
                        if (finalPoint != null)
                        {
                            Action<Point> simulateClick = fp =>
                            {
                                Log.InfoFormat("Performing mouse middle click at point ({0},{1}).", fp.X, fp.Y);
                                Action reinstateModifiers = () => { };
                                if (keyStateService.SimulateKeyStrokes
                                    && Settings.Default.SuppressModifierKeysForAllMouseActions)
                                {
                                    reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                                }
                                audioService.PlaySound(Settings.Default.MouseClickSoundFile, Settings.Default.MouseClickSoundVolume);
                                mouseOutputService.MoveAndMiddleClick(fp, true);
                                reinstateModifiers();
                            };
                            lastMouseActionStateManager.LastMouseAction = () => simulateClick(finalPoint.Value);
                            ShowCursor = false; //Hide cursor popup before performing action as it is possible for it to be performed on the popup
                            simulateClick(finalPoint.Value);
                        }

                        ResetAndCleanupAfterMouseAction();
                        resumeLookToScroll();
                    });
                    break;
                        
                case FunctionKeys.MouseMoveAndRightClick:
                    Log.Info("Mouse move and right click selected.");
                    resumeLookToScroll = SuspendLookToScrollWhileChoosingPointForMouse();
                    SetupFinalClickAction(finalPoint =>
                    {
                        if (finalPoint != null)
                        {
                            Action<Point> simulateClick = fp =>
                            {
                                Log.InfoFormat("Performing mouse right click at point ({0},{1}).", fp.X, fp.Y);
                                Action reinstateModifiers = () => { };
                                if (keyStateService.SimulateKeyStrokes
                                    && Settings.Default.SuppressModifierKeysForAllMouseActions)
                                {
                                    reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                                }
                                audioService.PlaySound(Settings.Default.MouseClickSoundFile, Settings.Default.MouseClickSoundVolume);
                                mouseOutputService.MoveAndRightClick(fp, true);
                                reinstateModifiers();
                            };
                            lastMouseActionStateManager.LastMouseAction = () => simulateClick(finalPoint.Value);
                            ShowCursor = false; //Hide cursor popup before performing action as it is possible for it to be performed on the popup
                            simulateClick(finalPoint.Value);
                        }

                        ResetAndCleanupAfterMouseAction();
                        resumeLookToScroll();
                    });
                    break;

                case FunctionKeys.MouseMoveAmountInPixels:
                    Log.Info("Progressing MouseMoveAmountInPixels.");
                    switch (Settings.Default.MouseMoveAmountInPixels)
                    {
                        case 1:
                            Settings.Default.MouseMoveAmountInPixels = 5;
                            break;

                        case 5:
                            Settings.Default.MouseMoveAmountInPixels = 10;
                            break;

                        case 10:
                            Settings.Default.MouseMoveAmountInPixels = 25;
                            break;

                        case 25:
                            Settings.Default.MouseMoveAmountInPixels = 50;
                            break;

                        case 50:
                            Settings.Default.MouseMoveAmountInPixels = 100;
                            break;

                        default:
                            Settings.Default.MouseMoveAmountInPixels = 1;
                            break;
                    }
                    break;

                case FunctionKeys.MouseMoveAndScrollToBottom:
                    Log.Info("Mouse move and scroll to bottom selected.");
                    resumeLookToScroll = SuspendLookToScrollWhileChoosingPointForMouse();
                    SetupFinalClickAction(finalPoint =>
                    {
                        if (finalPoint != null)
                        {
                            Action<Point> simulateScrollToBottom = fp =>
                            {
                                Log.InfoFormat("Performing mouse scroll to bottom at point ({0},{1}).", fp.X, fp.Y);
                                Action reinstateModifiers = () => { };
                                if (keyStateService.SimulateKeyStrokes
                                    && Settings.Default.SuppressModifierKeysForAllMouseActions)
                                {
                                    reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                                }
                                audioService.PlaySound(Settings.Default.MouseScrollSoundFile, Settings.Default.MouseScrollSoundVolume);
                                mouseOutputService.MoveAndScrollWheelDown(fp, Settings.Default.MouseScrollAmountInClicks, true);
                                reinstateModifiers();
                            };
                            lastMouseActionStateManager.LastMouseAction = () => simulateScrollToBottom(finalPoint.Value);
                            ShowCursor = false; //Hide cursor popup before performing action as it is possible for it to be performed on the popup
                            simulateScrollToBottom(finalPoint.Value);
                        }

                        ResetAndCleanupAfterMouseAction();
                        resumeLookToScroll();
                    }, suppressMagnification:true);
                    break;

                case FunctionKeys.MouseMoveAndScrollToLeft:
                    Log.Info("Mouse move and scroll to left selected.");
                    resumeLookToScroll = SuspendLookToScrollWhileChoosingPointForMouse();
                    SetupFinalClickAction(finalPoint =>
                    {
                        if (finalPoint != null)
                        {
                            Action<Point> simulateScrollToLeft = fp =>
                            {
                                Log.InfoFormat("Performing mouse scroll to left at point ({0},{1}).", fp.X, fp.Y);
                                Action reinstateModifiers = () => { };
                                if (keyStateService.SimulateKeyStrokes
                                    && Settings.Default.SuppressModifierKeysForAllMouseActions)
                                {
                                    reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                                }
                                audioService.PlaySound(Settings.Default.MouseScrollSoundFile, Settings.Default.MouseScrollSoundVolume);
                                mouseOutputService.MoveAndScrollWheelLeft(fp, Settings.Default.MouseScrollAmountInClicks, true);
                                reinstateModifiers();
                            };
                            lastMouseActionStateManager.LastMouseAction = () => simulateScrollToLeft(finalPoint.Value);
                            ShowCursor = false; //Hide cursor popup before performing action as it is possible for it to be performed on the popup
                            simulateScrollToLeft(finalPoint.Value);
                        }

                        ResetAndCleanupAfterMouseAction();
                        resumeLookToScroll();
                    }, suppressMagnification: true);
                    break;

                case FunctionKeys.MouseMoveAndScrollToRight:
                    Log.Info("Mouse move and scroll to right selected.");
                    resumeLookToScroll = SuspendLookToScrollWhileChoosingPointForMouse();
                    SetupFinalClickAction(finalPoint =>
                    {
                        if (finalPoint != null)
                        {
                            Action<Point> simulateScrollToRight = fp =>
                            {
                                Log.InfoFormat("Performing mouse scroll to right at point ({0},{1}).", fp.X, fp.Y);
                                Action reinstateModifiers = () => { };
                                if (keyStateService.SimulateKeyStrokes
                                    && Settings.Default.SuppressModifierKeysForAllMouseActions)
                                {
                                    reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                                }
                                audioService.PlaySound(Settings.Default.MouseScrollSoundFile, Settings.Default.MouseScrollSoundVolume);
                                mouseOutputService.MoveAndScrollWheelRight(fp, Settings.Default.MouseScrollAmountInClicks, true);
                                reinstateModifiers();
                            };
                            lastMouseActionStateManager.LastMouseAction = () => simulateScrollToRight(finalPoint.Value);
                            ShowCursor = false; //Hide cursor popup before performing action as it is possible for it to be performed on the popup
                            simulateScrollToRight(finalPoint.Value);
                        }

                        ResetAndCleanupAfterMouseAction();
                        resumeLookToScroll();
                    }, suppressMagnification: true);
                    break;

                case FunctionKeys.MouseMoveAndScrollToTop:
                    Log.Info("Mouse move and scroll to top selected.");
                    resumeLookToScroll = SuspendLookToScrollWhileChoosingPointForMouse();
                    SetupFinalClickAction(finalPoint =>
                    {
                        if (finalPoint != null)
                        {
                            Action<Point> simulateScrollToTop = fp =>
                            {
                                Log.InfoFormat("Performing mouse scroll to top at point ({0},{1}).", fp.X, fp.Y);
                                Action reinstateModifiers = () => { };
                                if (keyStateService.SimulateKeyStrokes
                                    && Settings.Default.SuppressModifierKeysForAllMouseActions)
                                {
                                    reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                                }
                                audioService.PlaySound(Settings.Default.MouseScrollSoundFile, Settings.Default.MouseScrollSoundVolume);
                                mouseOutputService.MoveAndScrollWheelUp(fp, Settings.Default.MouseScrollAmountInClicks, true);
                                reinstateModifiers();
                            };
                            lastMouseActionStateManager.LastMouseAction = () => simulateScrollToTop(finalPoint.Value);
                            ShowCursor = false; //Hide cursor popup before performing action as it is possible for it to be performed on the popup
                            simulateScrollToTop(finalPoint.Value);
                        }

                        ResetAndCleanupAfterMouseAction();
                        resumeLookToScroll();
                    }, suppressMagnification: true);
                    break;

                    case FunctionKeys.MouseScrollToTop:

                        var currentPoint = mouseOutputService.GetCursorPosition();
                        Log.InfoFormat("Mouse scroll to top selected at point ({0},{1}).", currentPoint.X, currentPoint.Y);
                        Action<Point?> performScroll = point =>
                        {
                            if (point != null)
                            {
                                Action<Point> simulateScrollToTop = fp =>
                                {
                                    Log.InfoFormat("Performing mouse scroll to top at point ({0},{1}).", fp.X, fp.Y);
                                    audioService.PlaySound(Settings.Default.MouseScrollSoundFile, Settings.Default.MouseScrollSoundVolume);
                                    mouseOutputService.MoveAndScrollWheelUp(fp, Settings.Default.MouseScrollAmountInClicks, true);
                                };
                                lastMouseActionStateManager.LastMouseAction = () => simulateScrollToTop(point.Value);
                                simulateScrollToTop(point.Value);
                            }
                        };
                        performScroll(currentPoint);
                        ResetAndCleanupAfterMouseAction();

                        break;

                    case FunctionKeys.MouseScrollToBottom:

                        var currentPointScroll = mouseOutputService.GetCursorPosition();
                        Log.InfoFormat("Mouse scroll to top selected at point ({0},{1}).", currentPointScroll.X, currentPointScroll.Y);
                        Action<Point?> performScrollDown = point =>
                        {
                            if (point != null)
                            {
                                Action<Point> simulateScrollToBottom = fp =>
                                {
                                    Log.InfoFormat("Performing mouse scroll to top at point ({0},{1}).", fp.X, fp.Y);
                                    audioService.PlaySound(Settings.Default.MouseScrollSoundFile, Settings.Default.MouseScrollSoundVolume);
                                    mouseOutputService.MoveAndScrollWheelDown(fp, Settings.Default.MouseScrollAmountInClicks, true);
                                };
                                lastMouseActionStateManager.LastMouseAction = () => simulateScrollToBottom(point.Value);
                                simulateScrollToBottom(point.Value);
                            }
                        };
                        performScrollDown(currentPointScroll);
                        ResetAndCleanupAfterMouseAction();

                        break;

                case FunctionKeys.MouseMoveTo:
                    Log.Info("Mouse move to selected.");
                    resumeLookToScroll = SuspendLookToScrollWhileChoosingPointForMouse();
                    SetupFinalClickAction(finalPoint =>
                    {
                        if (finalPoint != null)
                        {
                            Action<Point> simulateMoveTo = fp =>
                            {
                                Log.InfoFormat("Performing mouse move to point ({0},{1}).", fp.X, fp.Y);
                                Action reinstateModifiers = () => { };
                                if (keyStateService.SimulateKeyStrokes
                                    && Settings.Default.SuppressModifierKeysForAllMouseActions)
                                {
                                    reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                                }
                                mouseOutputService.MoveTo(fp);
                                reinstateModifiers();
                            };
                            lastMouseActionStateManager.LastMouseAction = () => simulateMoveTo(finalPoint.Value);
                            simulateMoveTo(finalPoint.Value);
                        }
                        ResetAndCleanupAfterMouseAction();
                        resumeLookToScroll();
                    });
                    break;

                case FunctionKeys.MouseMoveToBottom:
                    Log.Info("Mouse move to bottom selected.");
                    Action simulateMoveToBottom = () =>
                    {
                        var cursorPosition = mouseOutputService.GetCursorPosition();
                        var moveToPoint = new Point(cursorPosition.X, cursorPosition.Y + Settings.Default.MouseMoveAmountInPixels);
                        Log.InfoFormat("Performing mouse move to point ({0},{1}).", moveToPoint.X, moveToPoint.Y);
                        Action reinstateModifiers = () => { };
                        if (keyStateService.SimulateKeyStrokes
                            && Settings.Default.SuppressModifierKeysForAllMouseActions)
                        {
                            reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                        }
                        mouseOutputService.MoveTo(moveToPoint);
                        reinstateModifiers();
                    };
                    lastMouseActionStateManager.LastMouseAction = simulateMoveToBottom;
                    simulateMoveToBottom();
                    break;

                case FunctionKeys.MouseMoveToLeft:
                    Log.Info("Mouse move to left selected.");
                    Action simulateMoveToLeft = () =>
                    {
                        var cursorPosition = mouseOutputService.GetCursorPosition();
                        var moveToPoint = new Point(cursorPosition.X - Settings.Default.MouseMoveAmountInPixels, cursorPosition.Y);
                        Log.InfoFormat("Performing mouse move to point ({0},{1}).", moveToPoint.X, moveToPoint.Y);
                        Action reinstateModifiers = () => { };
                        if (keyStateService.SimulateKeyStrokes
                            && Settings.Default.SuppressModifierKeysForAllMouseActions)
                        {
                            reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                        }
                        mouseOutputService.MoveTo(moveToPoint);
                        reinstateModifiers();
                    };
                    lastMouseActionStateManager.LastMouseAction = simulateMoveToLeft;
                    simulateMoveToLeft();
                    break;

                case FunctionKeys.MouseMoveToRight:
                    Log.Info("Mouse move to right selected.");
                    Action simulateMoveToRight = () =>
                    {
                        var cursorPosition = mouseOutputService.GetCursorPosition();
                        var moveToPoint = new Point(cursorPosition.X + Settings.Default.MouseMoveAmountInPixels, cursorPosition.Y);
                        Log.InfoFormat("Performing mouse move to point ({0},{1}).", moveToPoint.X, moveToPoint.Y);
                        Action reinstateModifiers = () => { };
                        if (keyStateService.SimulateKeyStrokes
                            && Settings.Default.SuppressModifierKeysForAllMouseActions)
                        {
                            reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                        }
                        mouseOutputService.MoveTo(moveToPoint);
                        reinstateModifiers();
                    };
                    lastMouseActionStateManager.LastMouseAction = simulateMoveToRight;
                    simulateMoveToRight();
                    break;

                case FunctionKeys.MouseMoveToTop:
                    Log.Info("Mouse move to top selected.");
                    Action simulateMoveToTop = () =>
                    {
                        var cursorPosition = mouseOutputService.GetCursorPosition();
                        var moveToPoint = new Point(cursorPosition.X, cursorPosition.Y - Settings.Default.MouseMoveAmountInPixels);
                        Log.InfoFormat("Performing mouse move to point ({0},{1}).", moveToPoint.X, moveToPoint.Y);
                        Action reinstateModifiers = () => { };
                        if (keyStateService.SimulateKeyStrokes
                            && Settings.Default.SuppressModifierKeysForAllMouseActions)
                        {
                            reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                        }
                        mouseOutputService.MoveTo(moveToPoint);
                        reinstateModifiers();
                    };
                    lastMouseActionStateManager.LastMouseAction = simulateMoveToTop;
                    simulateMoveToTop();
                    break;

                case FunctionKeys.MouseRightClick:
                    var rightClickPoint = mouseOutputService.GetCursorPosition();
                    Log.InfoFormat("Mouse right click selected at point ({0},{1}).", rightClickPoint.X, rightClickPoint.Y);
                    Action performRightClick = () =>
                    {
                        Log.InfoFormat("Performing mouse right click at point ({0},{1}).", rightClickPoint.X, rightClickPoint.Y);
                        Action reinstateModifiers = () => { };
                        if (keyStateService.SimulateKeyStrokes
                            && Settings.Default.SuppressModifierKeysForAllMouseActions)
                        {
                            reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                        }
                        mouseOutputService.MoveTo(rightClickPoint);
                        audioService.PlaySound(Settings.Default.MouseClickSoundFile, Settings.Default.MouseClickSoundVolume);
                        mouseOutputService.RightButtonClick();
                        reinstateModifiers();
                    };
                    lastMouseActionStateManager.LastMouseAction = () => performRightClick();
                    performRightClick();
                    break;

                case FunctionKeys.MouseRightDownUp:
                    var rightDownUpPoint = mouseOutputService.GetCursorPosition();
                    if (keyStateService.KeyDownStates[KeyValues.MouseRightDownUpKey].Value.IsDownOrLockedDown())
                    {
                        Log.InfoFormat("Pressing mouse right button down at point ({0},{1}).", rightDownUpPoint.X, rightDownUpPoint.Y);
                        Action reinstateModifiers = () => { };
                        if (keyStateService.SimulateKeyStrokes
                            && Settings.Default.SuppressModifierKeysForAllMouseActions)
                        {
                            reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                        }
                        audioService.PlaySound(Settings.Default.MouseDownSoundFile, Settings.Default.MouseDownSoundVolume);
                        mouseOutputService.RightButtonDown();
                        reinstateModifiers();
                        lastMouseActionStateManager.LastMouseAction = null;
                    }
                    else
                    {
                        Log.InfoFormat("Releasing mouse right button at point ({0},{1}).", rightDownUpPoint.X, rightDownUpPoint.Y);
                        Action reinstateModifiers = () => { };
                        if (keyStateService.SimulateKeyStrokes
                            && Settings.Default.SuppressModifierKeysForAllMouseActions)
                        {
                            reinstateModifiers = keyStateService.ReleaseModifiers(Log);
                        }
                        audioService.PlaySound(Settings.Default.MouseUpSoundFile, Settings.Default.MouseUpSoundVolume);
                        mouseOutputService.RightButtonUp();
                        reinstateModifiers();
                        lastMouseActionStateManager.LastMouseAction = null;
                    }
                    break;

                case FunctionKeys.MoveAndResizeAdjustmentAmount:
                    Log.Info("Progressing MoveAndResizeAdjustmentAmount.");
                    switch (Settings.Default.MoveAndResizeAdjustmentAmountInPixels)
                    {
                        case 1:
                            Settings.Default.MoveAndResizeAdjustmentAmountInPixels = 5;
                            break;

                        case 5:
                            Settings.Default.MoveAndResizeAdjustmentAmountInPixels = 10;
                            break;

                        case 10:
                            Settings.Default.MoveAndResizeAdjustmentAmountInPixels = 25;
                            break;

                        case 25:
                            Settings.Default.MoveAndResizeAdjustmentAmountInPixels = 50;
                            break;

                        case 50:
                            Settings.Default.MoveAndResizeAdjustmentAmountInPixels = 100;
                            break;

                        default:
                            Settings.Default.MoveAndResizeAdjustmentAmountInPixels = 1;
                            break;
                    }
                    break;

                case FunctionKeys.MouseScrollAmountInClicks:
                    Log.Info("Progressing MouseScrollAmountInClicks.");
                    switch (Settings.Default.MouseScrollAmountInClicks)
                    {
                        case 1:
                            Settings.Default.MouseScrollAmountInClicks = 3;
                            break;

                        case 3:
                            Settings.Default.MouseScrollAmountInClicks = 5;
                            break;

                        case 5:
                            Settings.Default.MouseScrollAmountInClicks = 10;
                            break;

                        case 10:
                            Settings.Default.MouseScrollAmountInClicks = 25;
                            break;

                        default:
                            Settings.Default.MouseScrollAmountInClicks = 1;
                            break;
                    }
                    break;

                case FunctionKeys.MoveToBottom:
                    Log.InfoFormat("Moving to bottom by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Move(MoveToDirections.Bottom, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.MoveToBottomAndLeft:
                    Log.InfoFormat("Moving to bottom and left by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Move(MoveToDirections.BottomLeft, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.MoveToBottomAndLeftBoundaries:
                    Log.Info("Moving to bottom and left boundaries.");
                    mainWindowManipulationService.Move(MoveToDirections.BottomLeft, null);
                    break;

                case FunctionKeys.MoveToBottomAndRight:
                    Log.InfoFormat("Moving to bottom and right by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Move(MoveToDirections.BottomRight, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.MoveToBottomAndRightBoundaries:
                    Log.Info("Moving to bottom and right boundaries.");
                    mainWindowManipulationService.Move(MoveToDirections.BottomRight, null);
                    break;

                case FunctionKeys.MoveToBottomBoundary:
                    Log.Info("Moving to bottom boundary.");
                    mainWindowManipulationService.Move(MoveToDirections.Bottom, null);
                    break;

                case FunctionKeys.MoveToLeft:
                    Log.InfoFormat("Moving to left by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Move(MoveToDirections.Left, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.MoveToLeftBoundary:
                    Log.Info("Moving to left boundary.");
                    mainWindowManipulationService.Move(MoveToDirections.Left, null);
                    break;

                case FunctionKeys.MoveToRight:
                    Log.InfoFormat("Moving to right by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Move(MoveToDirections.Right, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.MoveToRightBoundary:
                    Log.Info("Moving to right boundary.");
                    mainWindowManipulationService.Move(MoveToDirections.Right, null);
                    break;

                case FunctionKeys.MoveToTop:
                    Log.InfoFormat("Moving to top by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Move(MoveToDirections.Top, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.MoveToTopAndLeft:
                    Log.InfoFormat("Moving to top and left by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Move(MoveToDirections.TopLeft, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.MoveToTopAndLeftBoundaries:
                    Log.Info("Moving to top and left boundaries.");
                    mainWindowManipulationService.Move(MoveToDirections.TopLeft, null);
                    break;

                case FunctionKeys.MoveToTopAndRight:
                    Log.InfoFormat("Moving to top and right by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Move(MoveToDirections.TopRight, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.MoveToTopAndRightBoundaries:
                    Log.Info("Moving to top and right boundaries.");
                    mainWindowManipulationService.Move(MoveToDirections.TopRight, null);
                    break;

                case FunctionKeys.MoveToTopBoundary:
                    Log.Info("Moving to top boundary.");
                    mainWindowManipulationService.Move(MoveToDirections.Top, null);
                    break;
                        
                case FunctionKeys.NextSuggestions:
                    Log.Info("Incrementing suggestions page.");

                    if (suggestionService.Suggestions != null
                        && (suggestionService.Suggestions.Count > (suggestionService.SuggestionsPage + 1) * SuggestionService.SuggestionsPerPage))
                    {
                        suggestionService.SuggestionsPage++;
                    }
                    break;

                case FunctionKeys.NoQuestionResult:
                    HandleYesNoQuestionResult(false);
                    break;

                case FunctionKeys.NumericAndSymbols1Keyboard:
                    Log.Info("Changing keyboard to NumericAndSymbols1.");
                    Keyboard = new NumericAndSymbols1();
                    break;

                case FunctionKeys.NumericAndSymbols2Keyboard:
                    Log.Info("Changing keyboard to NumericAndSymbols2.");
                    Keyboard = new NumericAndSymbols2();
                    break;

                case FunctionKeys.NumericAndSymbols3Keyboard:
                    Log.Info("Changing keyboard to Symbols3.");
                    Keyboard = new NumericAndSymbols3();
                    break;

                case FunctionKeys.PhysicalKeysKeyboard:
                    Log.Info("Changing keyboard to PhysicalKeys.");
                    Keyboard = new PhysicalKeys();
                    break;

                case FunctionKeys.PolishPoland:
                    SelectLanguage(Languages.PolishPoland);
                    break;

                case FunctionKeys.PortuguesePortugal:
                    SelectLanguage(Languages.PortuguesePortugal);
                    break;

                case FunctionKeys.PreviousSuggestions:
                    Log.Info("Decrementing suggestions page.");

                    if (suggestionService.SuggestionsPage > 0)
                    {
                        suggestionService.SuggestionsPage--;
                    }
                    break;

                case FunctionKeys.Quit:
                    Log.Info("Quit key selected.");
                    var keyboardBeforeQuit = Keyboard;
                    Keyboard = new YesNoQuestion(Resources.QUIT_MESSAGE,
                        () =>
                        {
                            Keyboard = new YesNoQuestion(Resources.QUIT_CONFIRMATION_MESSAGE,
                                () => Application.Current.Shutdown(),
                                () => { Keyboard = keyboardBeforeQuit; });
                        },
                        () => { Keyboard = keyboardBeforeQuit; });
                    break;

                case FunctionKeys.RepeatLastMouseAction:
                    if (lastMouseActionStateManager.LastMouseAction != null)
                    {
                        lastMouseActionStateManager.LastMouseAction();
                    }
                    break;

                case FunctionKeys.RussianRussia:
                    SelectLanguage(Languages.RussianRussia);
                    break;

                case FunctionKeys.ShrinkFromBottom:
                    Log.InfoFormat("Shrinking from bottom by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Shrink(ShrinkFromDirections.Bottom, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.ShrinkFromBottomAndLeft:
                    Log.InfoFormat("Shrinking from bottom and left by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Shrink(ShrinkFromDirections.BottomLeft, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.ShrinkFromBottomAndRight:
                    Log.InfoFormat("Shrinking from bottom and right by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Shrink(ShrinkFromDirections.BottomRight, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.ShrinkFromLeft:
                    Log.InfoFormat("Shrinking from left by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Shrink(ShrinkFromDirections.Left, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.ShrinkFromRight:
                    Log.InfoFormat("Shrinking from right by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Shrink(ShrinkFromDirections.Right, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.ShrinkFromTop:
                    Log.InfoFormat("Shrinking from top by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Shrink(ShrinkFromDirections.Top, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.ShrinkFromTopAndLeft:
                    Log.InfoFormat("Shrinking from top and left by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Shrink(ShrinkFromDirections.TopLeft, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.ShrinkFromTopAndRight:
                    Log.InfoFormat("Shrinking from top and right by {0}px.", Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    mainWindowManipulationService.Shrink(ShrinkFromDirections.TopRight, Settings.Default.MoveAndResizeAdjustmentAmountInPixels);
                    break;

                case FunctionKeys.SizeAndPositionKeyboard:
                    Log.Info("Changing keyboard to Size & Position.");
                    Keyboard = new SizeAndPosition(() => Keyboard = currentKeyboard);
                    break;

                case FunctionKeys.SlovakSlovakia:
                    SelectLanguage(Languages.SlovakSlovakia);
                    break;

                case FunctionKeys.SlovenianSlovenia:
                    SelectLanguage(Languages.SlovenianSlovenia);
                    break;

                case FunctionKeys.SpanishSpain:
                    SelectLanguage(Languages.SpanishSpain);
                    break;

                case FunctionKeys.Speak:
                    var speechStarted = audioService.SpeakNewOrInterruptCurrentSpeech(
                        keyboardOutputService.Text,
                        () => { KeyStateService.KeyDownStates[KeyValues.SpeakKey].Value = KeyDownStates.Up; },
                        Settings.Default.SpeechVolume,
                        Settings.Default.SpeechRate,
                        Settings.Default.SpeechVoice);
                    KeyStateService.KeyDownStates[KeyValues.SpeakKey].Value = speechStarted ? KeyDownStates.Down : KeyDownStates.Up;
                    break;

                case FunctionKeys.ConversationConfirmYes:
                    var speechStartedYes = audioService.SpeakNewOrInterruptCurrentSpeech(
                        Resources.YES,
                        () => { KeyStateService.KeyDownStates[KeyValues.SpeakKey].Value = KeyDownStates.Up; },
                        Settings.Default.SpeechVolume,
                        Settings.Default.SpeechRate,
                        Settings.Default.SpeechVoice);
                    KeyStateService.KeyDownStates[KeyValues.SpeakKey].Value = speechStartedYes ? KeyDownStates.Down : KeyDownStates.Up;
                    break;

                case FunctionKeys.ConversationConfirmNo:
                    var speechStartedNo = audioService.SpeakNewOrInterruptCurrentSpeech(
                        Resources.NO,
                        () => { KeyStateService.KeyDownStates[KeyValues.SpeakKey].Value = KeyDownStates.Up; },
                        Settings.Default.SpeechVolume,
                        Settings.Default.SpeechRate,
                        Settings.Default.SpeechVoice);
                    KeyStateService.KeyDownStates[KeyValues.SpeakKey].Value = speechStartedNo ? KeyDownStates.Down : KeyDownStates.Up;
                    break;

                case FunctionKeys.TurkishTurkey:
                    SelectLanguage(Languages.TurkishTurkey);
                    break;

                case FunctionKeys.WebBrowsingKeyboard:
                    Log.Info("Changing keyboard to WebBrowsing.");
                    Keyboard = new WebBrowsing();
                    break;

                case FunctionKeys.DJControllerKeyboard:
                    Log.Info("Changing keyboard to DJ Controller.");
                    Keyboard = new DJController();
                    break;

                case FunctionKeys.YesQuestionResult:
                    HandleYesNoQuestionResult(true);
                    break;
            }

            keyboardOutputService.ProcessFunctionKey(singleKeyValue.FunctionKey.Value);
        }

        private void SetupFinalClickAction(Action<Point?> finalClickAction, bool finalClickInSeries = true, bool suppressMagnification = false)
        {
            nextPointSelectionAction = nextPoint =>
            {
                if (!suppressMagnification 
                    && keyStateService.KeyDownStates[KeyValues.MouseMagnifierKey].Value.IsDownOrLockedDown())
                {
                    ShowCursor = false; //Ensure cursor is not showing when MagnifyAtPoint is set because...
                    //1.This triggers a screen capture, which shouldn't have the cursor in it.
                    //2.Last popup open stays on top (I know the VM in MVVM shouldn't care about this, so pretend it's all reason 1).
                    MagnifiedPointSelectionAction = finalClickAction;
                    MagnifyAtPoint = nextPoint;
                    if (MagnifyAtPoint != null) //If the magnification fails then MagnifyAtPoint will be null
                    {
                        ShowCursor = true;
                    }
                }
                else
                {
                    finalClickAction(nextPoint);
                }

                if (finalClickInSeries)
                {
                    nextPointSelectionAction = null;
                }
            };

            SelectionMode = SelectionModes.Point;
            ShowCursor = true;
        }

        private void ResetAndCleanupAfterMouseAction()
        {
            SelectionMode = SelectionModes.Key;
            nextPointSelectionAction = null;
            ShowCursor = false;
            MagnifyAtPoint = null;
            MagnifiedPointSelectionAction = null;

            if (keyStateService.KeyDownStates[KeyValues.MouseMagnifierKey].Value == KeyDownStates.Down)
            {
                keyStateService.KeyDownStates[KeyValues.MouseMagnifierKey].Value = KeyDownStates.Up; //Release magnifier if down but not locked down
            }
        }

        private void HandleServiceError(object sender, Exception exception)
        {
            Log.Error("Error event received from service. Raising ErrorNotificationRequest and playing ErrorSoundFile (from settings)", exception);

            inputService.RequestSuspend();

            if (RaiseToastNotification(Resources.CRASH_TITLE, exception.Message, NotificationTypes.Error, () => inputService.RequestResume()))
            {
                audioService.PlaySound(Settings.Default.ErrorSoundFile, Settings.Default.ErrorSoundVolume);
            }
        }

        private void NavigateToMenu()
        {
            Log.Info("Changing keyboard to Menu.");
            Keyboard = new Menu(CreateBackAction());
        }

        private void NavigateToVoiceKeyboard()
        {
            List<string> voices = GetAvailableVoices();

            if (voices != null && voices.Any())
            {
                Log.Info("Changing keyboard to Voice.");
                Keyboard = new Voice(CreateBackAction(), voices);
            }
            else
            {
                Log.Warn("No voices available. Returning to menu.");
                NavigateToMenu();
            }
        }

        private void SelectLanguage(Languages language)
        {
            Log.Info("Changing keyboard language to " + language);
            InputService.RequestSuspend(); //Reloading the dictionary locks the UI thread, so suspend input service to prevent accidental selections until complete
            Settings.Default.KeyboardAndDictionaryLanguage = language;
            InputService.RequestResume();

            if (Settings.Default.DisplayVoicesWhenChangingKeyboardLanguage)
            {
                NavigateToVoiceKeyboard();
            }
            else
            {
                NavigateToMenu();
            }
        }
        
        private void SelectVoice(string voice)
        {
            if (Settings.Default.MaryTTSEnabled)
            {
                Log.Info("Changing Mary TTS voice to " + voice);
                Settings.Default.MaryTTSVoice = voice;
            }
            else
            {
                Log.Info("Changing speech voice to " + voice);
                Settings.Default.SpeechVoice = voice;
            }

            NavigateToMenu();
        }

        private void SendMIDIMessage(string message)
        {
            if (MIDIMessages.IsMIDIMessage(message))
            {
                Byte channel = MIDIMessages.GetChannel(message);
                Byte first = MIDIMessages.GetFirstByte(message);
                midiService.SendMessage(channel, first, 127);
                Thread.Sleep(100);
                midiService.SendMessage(channel, first, 0);
            }
        }

        private void ShowMore()
        {
            if (Keyboard is Voice)
            {
                var voiceKeyboard = Keyboard as Voice;

                Log.Info("Moving to next page of voices.");
                Keyboard = new Voice(CreateBackAction(), voiceKeyboard.RemainingVoices);
            }
        }
        
        private Action CreateBackAction()
        {
            IKeyboard previousKeyboard = Keyboard;
            return () => Keyboard = previousKeyboard;
        }

        private List<string> GetAvailableVoices()
        {
            try
            {
                return Settings.Default.MaryTTSEnabled
                    ? audioService.GetAvailableMaryTTSVoices()
                    : audioService.GetAvailableVoices();
            }
            catch (Exception e)
            {
                Log.Error("Failed to fetch available voices.", e);
                return null;
            }
        }
    }
}