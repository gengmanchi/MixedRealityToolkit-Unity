﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;
using UnityEngine.EventSystems;

#if UNITY_EDITOR || UNITY_WSA
using UnityEngine.VR.WSA.Input;
using UnityEngine.Windows.Speech;
#endif

namespace HoloToolkit.Unity.InputModule
{
    /// <summary>
    /// Input Manager is responsible for managing input sources and dispatching relevant events
    /// to the appropriate input handlers. 
    /// </summary>
    public class InputManager : Singleton<InputManager>
    {
        public event Action InputEnabled;
        public event Action InputDisabled;

        private readonly Stack<GameObject> modalInputStack = new Stack<GameObject>();
        private readonly Stack<GameObject> fallbackInputStack = new Stack<GameObject>();

        /// <summary>
        /// Global listeners listen to all events and ignore the fact that other components might have consumed them.
        /// </summary>
        private readonly List<GameObject> globalListeners = new List<GameObject>();

        private int disabledRefCount;

        private InputEventData inputEventData;
        private InputClickedEventData sourceClickedEventData;
        private SourceStateEventData sourceStateEventData;
        private SourceRotationEventData sourceRotationEventData;
        private SourcePositionEventData sourcePositionEventData;
        private ManipulationEventData manipulationEventData;
        private HoldEventData holdEventData;
        private NavigationEventData navigationEventData;
        private PointerSpecificEventData pointerSpecificEventData;
        private InputXYEventData inputXYEventData;
        private TriggerEventData triggerEventData;
        private SpeechKeywordRecognizedEventData speechKeywordRecognizedEventData;
        private DictationEventData dictationEventData;

        /// <summary>
        /// List of the input sources as detected by the input manager like hands or motion controllers.
        /// </summary>
        private readonly List<InputSourceInfo> detectedInputSources = new List<InputSourceInfo>();
        public readonly ReadOnlyCollection<InputSourceInfo> DetectedInputSources;

        public InputManager()
        {
            DetectedInputSources = new ReadOnlyCollection<InputSourceInfo>(detectedInputSources);
        }

        /// <summary>
        /// Indicates if input is currently enabled or not.
        /// </summary>
        public bool IsInputEnabled
        {
            get { return disabledRefCount <= 0; }
        }

        /// <summary>
        /// Should the Unity UI events be fired?
        /// </summary>
        public bool ShouldSendUnityUiEvents { get { return FocusManager.Instance.IsUnityUiFocusable; } }

        /// <summary>
        /// Push a game object into the modal input stack. Any input handlers
        /// on the game object are given priority to input events before any focused objects.
        /// </summary>
        /// <param name="inputHandler">The input handler to push</param>
        public void PushModalInputHandler(GameObject inputHandler)
        {
            modalInputStack.Push(inputHandler);
        }

        /// <summary>
        /// Remove the last game object from the modal input stack.
        /// </summary>
        public void PopModalInputHandler()
        {
            modalInputStack.Pop();
        }

        /// <summary>
        /// Clear all modal input handlers off the stack.
        /// </summary>
        public void ClearModalInputStack()
        {
            modalInputStack.Clear();
        }

        /// <summary>
        /// Adds a global listener that will receive all input events, regardless
        /// of which other game objects might have handled the event beforehand.
        /// </summary>
        /// <param name="listener">Listener to add.</param>
        public void AddGlobalListener(GameObject listener)
        {
            globalListeners.Add(listener);
        }

        /// <summary>
        /// Removes a global listener.
        /// </summary>
        /// <param name="listener">Listener to remove.</param>
        public void RemoveGlobalListener(GameObject listener)
        {
            globalListeners.Remove(listener);
        }

        /// <summary>
        /// Push a game object into the fallback input stack. Any input handlers on
        /// the game object are given input events when no modal or focused objects consume the event.
        /// </summary>
        /// <param name="inputHandler">The input handler to push</param>
        public void PushFallbackInputHandler(GameObject inputHandler)
        {
            fallbackInputStack.Push(inputHandler);
        }

        /// <summary>
        /// Remove the last game object from the fallback input stack.
        /// </summary>
        public void PopFallbackInputHandler()
        {
            fallbackInputStack.Pop();
        }

        /// <summary>
        /// Clear all fallback input handlers off the stack.
        /// </summary>
        public void ClearFallbackInputStack()
        {
            fallbackInputStack.Clear();
        }

        /// <summary>
        /// Push a disabled input state onto the input manager.
        /// While input is disabled no events will be sent out and the cursor displays
        /// a waiting animation.
        /// </summary>
        public void PushInputDisable()
        {
            ++disabledRefCount;

            if (disabledRefCount == 1)
            {
                InputDisabled.RaiseEvent();
            }
        }

        /// <summary>
        /// Pop disabled input state. When the last disabled state is 
        /// popped off the stack input will be re-enabled.
        /// </summary>
        public void PopInputDisable()
        {
            --disabledRefCount;
            Debug.Assert(disabledRefCount >= 0, "Tried to pop more input disable than the amount pushed.");

            if (disabledRefCount == 0)
            {
                InputEnabled.RaiseEvent();
            }
        }

        /// <summary>
        /// Clear the input disable stack, which will immediately re-enable input.
        /// </summary>
        public void ClearInputDisableStack()
        {
            bool wasInputDisabled = disabledRefCount > 0;
            disabledRefCount = 0;

            if (wasInputDisabled)
            {
                InputEnabled.RaiseEvent();
            }
        }

        /// <summary>
        /// Raise the event OnFocusEnter to the game object when focus enters it.
        /// </summary>
        /// <param name="focusedObject"></param>
        public void RaiseFocusEnter(GameObject focusedObject)
        {
            ExecuteEvents.ExecuteHierarchy(focusedObject, null, OnFocusEnterEventHandler);

            if (ShouldSendUnityUiEvents)
            {
                PointerInputEventData pointerInputEventData = FocusManager.Instance.BorrowPointerEventData();
                ExecuteEvents.ExecuteHierarchy(focusedObject, pointerInputEventData, ExecuteEvents.pointerEnterHandler);
            }
        }

        /// <summary>
        /// Raise the event OnFocusExit to the game object when focus exists it.
        /// </summary>
        /// <param name="focusedObject"></param>
        public void RaiseFocusExit(GameObject defocusedObject)
        {
            ExecuteEvents.ExecuteHierarchy(defocusedObject, null, OnFocusExitEventHandler);

            if (ShouldSendUnityUiEvents)
            {
                PointerInputEventData pointerInputEventData = FocusManager.Instance.BorrowPointerEventData();
                ExecuteEvents.ExecuteHierarchy(defocusedObject, pointerInputEventData, ExecuteEvents.pointerExitHandler);
            }
        }

        /// <summary>
        /// Raise focus enter and exit events for when a motion controller that supports pointing points to a game object.
        /// </summary>
        /// <param name="pointer"></param>
        /// <param name="oldFocusedObject"></param>
        /// <param name="newFocusedObject"></param>
        public void RaisePointerSpecificFocusChangedEvents(IPointingSource pointer, GameObject oldFocusedObject, GameObject newFocusedObject)
        {
            if (oldFocusedObject != null)
            {
                pointerSpecificEventData.Initialize(pointer);
                ExecuteEvents.ExecuteHierarchy(oldFocusedObject, pointerSpecificEventData, OnPointerSpecificFocusExitEventHandler);
            }

            if (newFocusedObject != null)
            {
                pointerSpecificEventData.Initialize(pointer);
                ExecuteEvents.ExecuteHierarchy(newFocusedObject, pointerSpecificEventData, OnPointerSpecificFocusEnterEventHandler);
            }
        }

        protected override void Awake()
        {
            base.Awake();
            InitializeEventDatas();
        }

        private void InitializeEventDatas()
        {
            inputEventData = new InputEventData(EventSystem.current);
            sourceClickedEventData = new InputClickedEventData(EventSystem.current);
            sourceStateEventData = new SourceStateEventData(EventSystem.current);
            sourceRotationEventData = new SourceRotationEventData(EventSystem.current);
            sourcePositionEventData = new SourcePositionEventData(EventSystem.current);
            manipulationEventData = new ManipulationEventData(EventSystem.current);
            navigationEventData = new NavigationEventData(EventSystem.current);
            holdEventData = new HoldEventData(EventSystem.current);
            pointerSpecificEventData = new PointerSpecificEventData(EventSystem.current);
            inputXYEventData = new InputXYEventData(EventSystem.current);
            triggerEventData = new TriggerEventData(EventSystem.current);
        }

        public void HandleEvent<T>(BaseEventData eventData, ExecuteEvents.EventFunction<T> eventHandler)
            where T : IEventSystemHandler
        {
            if (!Instance.enabled || disabledRefCount > 0)
            {
                return;
            }

            Debug.Assert(!eventData.used);
            GameObject focusedObject = FocusManager.Instance.TryGetFocusedObject(eventData);

            // Send the event to global listeners
            for (int i = 0; i < globalListeners.Count; i++)
            {
                // Global listeners should only get events on themselves, as opposed to their hierarchy.
                ExecuteEvents.Execute(globalListeners[i], eventData, eventHandler);
            }

            if (eventData.used)
            {
                // All global listeners get a chance to see the event, but if any of them marked it used, we stop
                // the event from going any further.
                return;
            }

            // TODO: robertes: consider whether modal and fallback input should flow to each handler until used
            //       or it should flow to just the topmost handler on the stack as it does today.

            // Handle modal input if one exists
            if (modalInputStack.Count > 0)
            {
                GameObject modalInput = modalInputStack.Peek();

                // If there is a focused object in the hierarchy of the modal handler, start the event
                // bubble there
                if (focusedObject != null && modalInput != null && focusedObject.transform.IsChildOf(modalInput.transform))
                {
                    if (ExecuteEvents.ExecuteHierarchy(focusedObject, eventData, eventHandler) && eventData.used)
                    {
                        return;
                    }
                }
                // Otherwise, just invoke the event on the modal handler itself
                else
                {
                    if (ExecuteEvents.ExecuteHierarchy(modalInput, eventData, eventHandler) && eventData.used)
                    {
                        return;
                    }
                }
            }

            // If event was not handled by modal, pass it on to the current focused object
            if (focusedObject != null)
            {
                if (ExecuteEvents.ExecuteHierarchy(focusedObject, eventData, eventHandler) && eventData.used)
                {
                    return;
                }
            }

            // If event was not handled by the focused object, pass it on to any fallback handlers
            if (fallbackInputStack.Count > 0)
            {
                GameObject fallbackInput = fallbackInputStack.Peek();
                if (ExecuteEvents.ExecuteHierarchy(fallbackInput, eventData, eventHandler) && eventData.used)
                {
                    return;
                }
            }
        }

        private static readonly ExecuteEvents.EventFunction<IFocusable> OnFocusEnterEventHandler =
            delegate (IFocusable handler, BaseEventData eventData)
            {
                handler.OnFocusEnter();
            };

        private static readonly ExecuteEvents.EventFunction<IFocusable> OnFocusExitEventHandler =
            delegate (IFocusable handler, BaseEventData eventData)
            {
                handler.OnFocusExit();
            };

        private static readonly ExecuteEvents.EventFunction<IPointerSpecificFocusable> OnPointerSpecificFocusEnterEventHandler =
            delegate (IPointerSpecificFocusable handler, BaseEventData eventData)
            {
                PointerSpecificEventData casted = ExecuteEvents.ValidateEventData<PointerSpecificEventData>(eventData);
                handler.OnFocusEnter(casted);
            };

        private static readonly ExecuteEvents.EventFunction<IPointerSpecificFocusable> OnPointerSpecificFocusExitEventHandler =
            delegate (IPointerSpecificFocusable handler, BaseEventData eventData)
            {
                PointerSpecificEventData casted = ExecuteEvents.ValidateEventData<PointerSpecificEventData>(eventData);
                handler.OnFocusExit(casted);
            };

        private static readonly ExecuteEvents.EventFunction<IInputClickHandler> OnInputClickedEventHandler =
            delegate (IInputClickHandler handler, BaseEventData eventData)
            {
                InputClickedEventData casted = ExecuteEvents.ValidateEventData<InputClickedEventData>(eventData);
                handler.OnInputClicked(casted);
            };

        public void RaiseInputClicked(IInputSource source, uint sourceId, InteractionPressKind pressKind, int tapCount, object tag = null)
        {
            // Create input event
            sourceClickedEventData.Initialize(source, sourceId, tag, pressKind, tapCount);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(sourceClickedEventData, OnInputClickedEventHandler);

            // NOTE: In Unity UI, a "click" happens on every pointer up, so we have SourceUp call the pointerClickHandler.
        }

        private static readonly ExecuteEvents.EventFunction<IInputHandler> OnSourceUpEventHandler =
            delegate (IInputHandler handler, BaseEventData eventData)
            {
                InputEventData casted = ExecuteEvents.ValidateEventData<InputEventData>(eventData);
                handler.OnInputUp(casted);
            };

        public void RaiseSourceUp(IInputSource source, uint sourceId, InteractionPressKind pressKind, object tag = null)
        {
            // Create input event
            inputEventData.Initialize(source, sourceId, tag, pressKind);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(inputEventData, OnSourceUpEventHandler);

            // UI events
            if (ShouldSendUnityUiEvents && (pressKind == InteractionPressKind.Select))
            {
                PointerInputEventData pointerInputEventData = FocusManager.Instance.BorrowPointerEventData();
                pointerInputEventData.InputSource = source;
                pointerInputEventData.SourceId = sourceId;

                HandleEvent(pointerInputEventData, ExecuteEvents.pointerUpHandler);
                HandleEvent(pointerInputEventData, ExecuteEvents.pointerClickHandler);
            }
        }

        private static readonly ExecuteEvents.EventFunction<IInputHandler> OnSourceDownEventHandler =
            delegate (IInputHandler handler, BaseEventData eventData)
            {
                InputEventData casted = ExecuteEvents.ValidateEventData<InputEventData>(eventData);
                handler.OnInputDown(casted);
            };

        public void RaiseSourceDown(IInputSource source, uint sourceId, InteractionPressKind pressKind, object tag = null)
        {
            // Create input event
            inputEventData.Initialize(source, sourceId, tag, pressKind);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(inputEventData, OnSourceDownEventHandler);

            // UI events
            if (ShouldSendUnityUiEvents && (pressKind == InteractionPressKind.Select))
            {
                PointerInputEventData pointerInputEventData = FocusManager.Instance.BorrowPointerEventData();
                pointerInputEventData.InputSource = source;
                pointerInputEventData.SourceId = sourceId;

                pointerInputEventData.eligibleForClick = true;
                pointerInputEventData.delta = Vector2.zero;
                pointerInputEventData.dragging = false;
                pointerInputEventData.useDragThreshold = true;
                pointerInputEventData.pressPosition = pointerInputEventData.position;
                pointerInputEventData.pointerPressRaycast = pointerInputEventData.pointerCurrentRaycast;

                HandleEvent(pointerInputEventData, ExecuteEvents.pointerDownHandler);
            }
        }

        private static readonly ExecuteEvents.EventFunction<ISourceStateHandler> OnSourceDetectedEventHandler =
            delegate (ISourceStateHandler handler, BaseEventData eventData)
            {
                SourceStateEventData casted = ExecuteEvents.ValidateEventData<SourceStateEventData>(eventData);
                handler.OnSourceDetected(casted);
            };

        public void RaiseSourceDetected(IInputSource source, uint sourceId, object tag = null)
        {
            // Manage list of detected sources
            bool alreadyDetected = false;

            for (int iDetected = 0; iDetected < detectedInputSources.Count; iDetected++)
            {
                if (detectedInputSources[iDetected].Matches(source, sourceId))
                {
                    alreadyDetected = true;
                    break;
                }
            }

            if (!alreadyDetected)
            {
                detectedInputSources.Add(new InputSourceInfo(source, sourceId));
            }

            // Create input event
            sourceStateEventData.Initialize(source, sourceId, tag);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(sourceStateEventData, OnSourceDetectedEventHandler);
        }

        private static readonly ExecuteEvents.EventFunction<ISourceStateHandler> OnSourceLostEventHandler =
            delegate (ISourceStateHandler handler, BaseEventData eventData)
            {
                SourceStateEventData casted = ExecuteEvents.ValidateEventData<SourceStateEventData>(eventData);
                handler.OnSourceLost(casted);
            };

        public void RaiseSourceLost(IInputSource source, uint sourceId, object tag = null)
        {
            // Manage list of detected sources
            for (int iDetected = 0; iDetected < detectedInputSources.Count; iDetected++)
            {
                if (detectedInputSources[iDetected].Matches(source, sourceId))
                {
                    detectedInputSources.RemoveAt(iDetected);
                    break;
                }
            }

            // Create input event
            sourceStateEventData.Initialize(source, sourceId, tag);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(sourceStateEventData, OnSourceLostEventHandler);
        }

        #region Manipulation Events

        private static readonly ExecuteEvents.EventFunction<IManipulationHandler> OnManipulationStartedEventHandler =
            delegate (IManipulationHandler handler, BaseEventData eventData)
            {
                ManipulationEventData casted = ExecuteEvents.ValidateEventData<ManipulationEventData>(eventData);
                handler.OnManipulationStarted(casted);
            };

        public void RaiseManipulationStarted(IInputSource source, uint sourceId, Vector3 cumulativeDelta, object tag = null)
        {
            // Create input event
            manipulationEventData.Initialize(source, sourceId, tag, cumulativeDelta);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(manipulationEventData, OnManipulationStartedEventHandler);
        }

        private static readonly ExecuteEvents.EventFunction<IManipulationHandler> OnManipulationUpdatedEventHandler =
            delegate (IManipulationHandler handler, BaseEventData eventData)
            {
                ManipulationEventData casted = ExecuteEvents.ValidateEventData<ManipulationEventData>(eventData);
                handler.OnManipulationUpdated(casted);
            };

        public void RaiseManipulationUpdated(IInputSource source, uint sourceId, Vector3 cumulativeDelta, object tag = null)
        {
            // Create input event
            manipulationEventData.Initialize(source, sourceId, tag, cumulativeDelta);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(manipulationEventData, OnManipulationUpdatedEventHandler);
        }

        private static readonly ExecuteEvents.EventFunction<IManipulationHandler> OnManipulationCompletedEventHandler =
            delegate (IManipulationHandler handler, BaseEventData eventData)
            {
                ManipulationEventData casted = ExecuteEvents.ValidateEventData<ManipulationEventData>(eventData);
                handler.OnManipulationCompleted(casted);
            };

        public void RaiseManipulationCompleted(IInputSource source, uint sourceId, Vector3 cumulativeDelta, object tag = null)
        {
            // Create input event
            manipulationEventData.Initialize(source, sourceId, tag, cumulativeDelta);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(manipulationEventData, OnManipulationCompletedEventHandler);
        }

        private static readonly ExecuteEvents.EventFunction<IManipulationHandler> OnManipulationCanceledEventHandler =
            delegate (IManipulationHandler handler, BaseEventData eventData)
            {
                ManipulationEventData casted = ExecuteEvents.ValidateEventData<ManipulationEventData>(eventData);
                handler.OnManipulationCanceled(casted);
            };

        public void RaiseManipulationCanceled(IInputSource source, uint sourceId, Vector3 cumulativeDelta, object tag = null)
        {
            // Create input event
            manipulationEventData.Initialize(source, sourceId, tag, cumulativeDelta);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(manipulationEventData, OnManipulationCanceledEventHandler);
        }

        #endregion // Manipulation Events

        #region Hold Events

        private static readonly ExecuteEvents.EventFunction<IHoldHandler> OnHoldStartedEventHandler =
            delegate (IHoldHandler handler, BaseEventData eventData)
            {
                HoldEventData casted = ExecuteEvents.ValidateEventData<HoldEventData>(eventData);
                handler.OnHoldStarted(casted);
            };

        public void RaiseHoldStarted(IInputSource source, uint sourceId, object tag = null)
        {
            // Create input event
            holdEventData.Initialize(source, sourceId, tag);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(holdEventData, OnHoldStartedEventHandler);
        }

        private static readonly ExecuteEvents.EventFunction<IHoldHandler> OnHoldCompletedEventHandler =
            delegate (IHoldHandler handler, BaseEventData eventData)
            {
                HoldEventData casted = ExecuteEvents.ValidateEventData<HoldEventData>(eventData);
                handler.OnHoldCompleted(casted);
            };

        public void RaiseHoldCompleted(IInputSource source, uint sourceId, object tag = null)
        {
            // Create input event
            holdEventData.Initialize(source, sourceId, tag);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(holdEventData, OnHoldCompletedEventHandler);
        }

        private static readonly ExecuteEvents.EventFunction<IHoldHandler> OnHoldCanceledEventHandler =
            delegate (IHoldHandler handler, BaseEventData eventData)
            {
                HoldEventData casted = ExecuteEvents.ValidateEventData<HoldEventData>(eventData);
                handler.OnHoldCanceled(casted);
            };

        public void RaiseHoldCanceled(IInputSource source, uint sourceId, object tag = null)
        {
            // Create input event
            holdEventData.Initialize(source, sourceId, tag);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(holdEventData, OnHoldCanceledEventHandler);
        }

        #endregion // Hold Events

        #region Navigation Events

        private static readonly ExecuteEvents.EventFunction<INavigationHandler> OnNavigationStartedEventHandler =
            delegate (INavigationHandler handler, BaseEventData eventData)
            {
                NavigationEventData casted = ExecuteEvents.ValidateEventData<NavigationEventData>(eventData);
                handler.OnNavigationStarted(casted);
            };

        public void RaiseNavigationStarted(IInputSource source, uint sourceId, Vector3 normalizedOffset, object tag = null)
        {
            // Create input event
            navigationEventData.Initialize(source, sourceId, tag, normalizedOffset);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(navigationEventData, OnNavigationStartedEventHandler);
        }

        private static readonly ExecuteEvents.EventFunction<INavigationHandler> OnNavigationUpdatedEventHandler =
            delegate (INavigationHandler handler, BaseEventData eventData)
            {
                NavigationEventData casted = ExecuteEvents.ValidateEventData<NavigationEventData>(eventData);
                handler.OnNavigationUpdated(casted);
            };

        public void RaiseNavigationUpdated(IInputSource source, uint sourceId, Vector3 normalizedOffset, object tag = null)
        {
            // Create input event
            navigationEventData.Initialize(source, sourceId, tag, normalizedOffset);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(navigationEventData, OnNavigationUpdatedEventHandler);
        }

        private static readonly ExecuteEvents.EventFunction<INavigationHandler> OnNavigationCompletedEventHandler =
            delegate (INavigationHandler handler, BaseEventData eventData)
            {
                NavigationEventData casted = ExecuteEvents.ValidateEventData<NavigationEventData>(eventData);
                handler.OnNavigationCompleted(casted);
            };

        public void RaiseNavigationCompleted(IInputSource source, uint sourceId, Vector3 normalizedOffset, object tag = null)
        {
            // Create input event
            navigationEventData.Initialize(source, sourceId, tag, normalizedOffset);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(navigationEventData, OnNavigationCompletedEventHandler);
        }

        private static readonly ExecuteEvents.EventFunction<INavigationHandler> OnNavigationCanceledEventHandler =
            delegate (INavigationHandler handler, BaseEventData eventData)
            {
                NavigationEventData casted = ExecuteEvents.ValidateEventData<NavigationEventData>(eventData);
                handler.OnNavigationCanceled(casted);
            };

        public void RaiseNavigationCanceled(IInputSource source, uint sourceId, Vector3 normalizedOffset, object tag = null)
        {
            // Create input event
            navigationEventData.Initialize(source, sourceId, tag, normalizedOffset);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(navigationEventData, OnNavigationCanceledEventHandler);
        }

        #endregion // Navigation Events

        #region Controller Events

        private static readonly ExecuteEvents.EventFunction<IControllerInputHandler> OnInputXYChangedEventHandler =
            delegate (IControllerInputHandler handler, BaseEventData eventData)
            {
                InputXYEventData casted = ExecuteEvents.ValidateEventData<InputXYEventData>(eventData);
                handler.OnInputXYChanged(casted);
            };

        public void RaiseInputXYChanged(IInputSource source, uint sourceId, InteractionPressKind pressKind, double x, double y, object tag = null)
        {
            // Create input event
            inputXYEventData.Initialize(source, sourceId, tag, pressKind, x, y);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(inputXYEventData, OnInputXYChangedEventHandler);
        }

        private static readonly ExecuteEvents.EventFunction<IControllerInputHandler> OnTriggerPressedValueChangedEventHandler =
            delegate (IControllerInputHandler handler, BaseEventData eventData)
            {
                TriggerEventData casted = ExecuteEvents.ValidateEventData<TriggerEventData>(eventData);
                handler.OnTriggerPressedValueChanged(casted);
            };

        public void RaiseTriggerPressedValueChanged(IInputSource source, uint sourceId, double pressedValue, object tag = null)
        {
            // Create input event
            triggerEventData.Initialize(source, sourceId, tag, pressedValue);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(triggerEventData, OnTriggerPressedValueChangedEventHandler);
        }

        private static readonly ExecuteEvents.EventFunction<IControllerTouchpadHandler> OnTouchpadTouchedEventHandler =
            delegate (IControllerTouchpadHandler handler, BaseEventData eventData)
            {
                InputEventData casted = ExecuteEvents.ValidateEventData<InputEventData>(eventData);
                handler.OnTouchpadTouched(casted);
            };

        public void RaiseTouchpadTouched(IInputSource source, uint sourceId, object tag = null)
        {
            // Create input event
            inputEventData.Initialize(source, sourceId, tag, InteractionPressKind.Touchpad);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(inputEventData, OnTouchpadTouchedEventHandler);
        }

        private static readonly ExecuteEvents.EventFunction<IControllerTouchpadHandler> OnTouchpadReleasedEventHandler =
            delegate (IControllerTouchpadHandler handler, BaseEventData eventData)
            {
                InputEventData casted = ExecuteEvents.ValidateEventData<InputEventData>(eventData);
                handler.OnTouchpadReleased(casted);
            };

        public void RaiseTouchpadReleased(IInputSource source, uint sourceId, object tag = null)
        {
            // Create input event
            inputEventData.Initialize(source, sourceId, tag, InteractionPressKind.Touchpad);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(inputEventData, OnTouchpadReleasedEventHandler);
        }

        private static readonly ExecuteEvents.EventFunction<ISourcePositionHandler> OnSourcePositionChangedEventHandler =
            delegate (ISourcePositionHandler handler, BaseEventData eventData)
            {
                SourcePositionEventData casted = ExecuteEvents.ValidateEventData<SourcePositionEventData>(eventData);
                handler.OnPositionChanged(casted);
            };

        public void RaiseSourcePositionChanged(IInputSource source, uint sourceId, Vector3 position, object tag = null)
        {
            // Create input event
            sourcePositionEventData.Initialize(source, sourceId, tag, position);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(sourcePositionEventData, OnSourcePositionChangedEventHandler);
        }

        private static readonly ExecuteEvents.EventFunction<ISourceRotationHandler> OnSourceRotationChangedEventHandler =
            delegate (ISourceRotationHandler handler, BaseEventData eventData)
            {
                SourceRotationEventData casted = ExecuteEvents.ValidateEventData<SourceRotationEventData>(eventData);
                handler.OnRotationChanged(casted);
            };

        public void RaiseSourceRotationChanged(IInputSource source, uint sourceId, Quaternion rotation, object tag = null)
        {
            // Create input event
            sourceRotationEventData.Initialize(source, sourceId, tag, rotation);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(sourceRotationEventData, OnSourceRotationChangedEventHandler);
        }

        #endregion // Controller Events

        #region Speech Events

        private static readonly ExecuteEvents.EventFunction<ISpeechHandler> OnSpeechKeywordRecognizedEventHandler =
            delegate (ISpeechHandler handler, BaseEventData eventData)
            {
                SpeechKeywordRecognizedEventData casted = ExecuteEvents.ValidateEventData<SpeechKeywordRecognizedEventData>(eventData);
                handler.OnSpeechKeywordRecognized(casted);
            };

#if UNITY_EDITOR || UNITY_WSA
        public void RaiseSpeechKeywordPhraseRecognized(IInputSource source, uint sourceId, ConfidenceLevel confidence, TimeSpan phraseDuration, DateTime phraseStartTime, SemanticMeaning[] semanticMeanings, string text, object tag = null)
        {
            // Create input event
            speechKeywordRecognizedEventData.Initialize(source, sourceId, tag, confidence, phraseDuration, phraseStartTime, semanticMeanings, text);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(speechKeywordRecognizedEventData, OnSpeechKeywordRecognizedEventHandler);
        }
#endif

        #endregion // Speech Events

        #region Dictation Events

        private static readonly ExecuteEvents.EventFunction<IDictationHandler> OnDictationHypothesisEventHandler =
            delegate (IDictationHandler handler, BaseEventData eventData)
            {
                DictationEventData casted = ExecuteEvents.ValidateEventData<DictationEventData>(eventData);
                handler.OnDictationHypothesis(casted);
            };

        public void RaiseDictationHypothesis(IInputSource source, uint sourceId, string dictationHypothesis, AudioClip dictationAudioClip = null, object tag = null)
        {
            // Create input event
            dictationEventData.Initialize(source, sourceId, tag, dictationHypothesis, dictationAudioClip);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(dictationEventData, OnDictationHypothesisEventHandler);
        }

        private static readonly ExecuteEvents.EventFunction<IDictationHandler> OnDictationResultEventHandler =
            delegate (IDictationHandler handler, BaseEventData eventData)
            {
                DictationEventData casted = ExecuteEvents.ValidateEventData<DictationEventData>(eventData);
                handler.OnDictationResult(casted);
            };

        public void RaiseDictationResult(IInputSource source, uint sourceId, string dictationResult, AudioClip dictationAudioClip = null, object tag = null)
        {
            // Create input event
            dictationEventData.Initialize(source, sourceId, tag, dictationResult, dictationAudioClip);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(dictationEventData, OnDictationResultEventHandler);
        }

        private static readonly ExecuteEvents.EventFunction<IDictationHandler> OnDictationCompleteEventHandler =
            delegate (IDictationHandler handler, BaseEventData eventData)
            {
                DictationEventData casted = ExecuteEvents.ValidateEventData<DictationEventData>(eventData);
                handler.OnDictationComplete(casted);
            };

        public void RaiseDictationComplete(IInputSource source, uint sourceId, string dictationResult, AudioClip dictationAudioClip, object tag = null)
        {
            // Create input event
            dictationEventData.Initialize(source, sourceId, tag, dictationResult, dictationAudioClip);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(dictationEventData, OnDictationCompleteEventHandler);
        }

        private static readonly ExecuteEvents.EventFunction<IDictationHandler> OnDictationErrorEventHandler =
            delegate (IDictationHandler handler, BaseEventData eventData)
            {
                DictationEventData casted = ExecuteEvents.ValidateEventData<DictationEventData>(eventData);
                handler.OnDictationError(casted);
            };

        public void RaiseDictationError(IInputSource source, uint sourceId, string dictationResult, AudioClip dictationAudioClip = null, object tag = null)
        {
            // Create input event
            dictationEventData.Initialize(source, sourceId, tag, dictationResult, dictationAudioClip);

            // Pass handler through HandleEvent to perform modal/fallback logic
            HandleEvent(dictationEventData, OnDictationErrorEventHandler);
        }

        #endregion // Dictation Events
    }
}