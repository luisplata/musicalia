using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

// Tipos públicos usados por TouchDebugger y otros visualizadores
public enum InputType { Tap, LongPress, Drag, Swipe, Unknown }

public struct EndedEvent
{
    public int id;
    public InputType type;
    public Vector2 position;
    public float duration;
    public float distance;
    public Vector2 startPos;
}

// Delegados públicos para eventos, evitando problemas de accesibilidad dentro de la clase
public delegate void EndedEventDelegate(EndedEvent ev);
public delegate void MultiTouchDelegate(List<EndedEvent> events);
// Drag-specific delegates
public delegate void DragStartDelegate(int id, Vector2 pos);
public delegate void DragMoveDelegate(int id, Vector2 pos);
public delegate void DragEndDelegate(EndedEvent ev);

public class TouchDebugger : MonoBehaviour
{
    [Header("Umbrales (ajustables desde el Inspector)")]
    public float tapMaxDuration = 0.25f;
    public float tapMaxMove = 10f;
    public float longPressTime = 0.6f;
    public float longPressMoveTolerance = 10f;

    // Valores finales recomendados (ajustables en Inspector):
    // - Swipes: requieren recorrido y velocidad moderados.
    // - Drags: movimientos lentos y sostenidos (fáciles de generar con mouse lento).
    // Final tuned defaults (prefer drags in Editor):
    public float swipeMinDistance = 200f;      // px: swipe requires a long travel
    public float swipeMaxDuration = 0.35f;     // s: swipe must be relatively quick
    public float swipeSpeedThreshold = 800f;   // px/s: swipe must be relatively fast
    // Hacer drag más estricto para evitar falsas detecciones en Editor
    public float dragHoldTime = 0.08f;         // s: tiempo mínimo sostenido antes de marcar drag
    public float dragStartDistance = 8f;       // px: distancia mínima para empezar drag
    // Reglas adicionales solicitadas por el usuario
    public float initialHoldForDrag = 0.2f;    // tiempo inicial antes del movimiento que caracteriza un 'tap sostenido'
    public float dragMinDistance = 100f;      // px: distancia mínima para considerar un Drag según la nueva regla
    public float dragMinTotalDuration = 0.5f; // s: duración total mínima para considerar Drag según la nueva regla

    [Header("Debug / Simulación")]
    // Use Touch Simulation (maps mouse to EnhancedTouch) and avoid double-processing mouse
    public bool enableMouseInEditor = false; // procesar mouse directamente desactivado cuando usamos TouchSimulation
    public bool useTouchSimulationInEditor = true; // si true, TouchSimulation se habilita y mapea mouse->touch

    [Header("Overlay de depuración")]
    public bool showOnscreenLog = true; // muestra los eventos en pantalla (funciona en Editor y en el dispositivo)
    public int maxOnscreenMessages = 12;

    [Header("Visual")]
    public bool showTouchIndicators = true; // dibuja cajas en pantalla donde hay toques (útil en dispositivo)

    [Header("SFX (asignar AudioClips en el Inspector)")]
    public AudioSource sfxSource; // si no se asigna, se creará uno automáticamente
    [Header("Debug")]
    [Tooltip("Si true mostrará logs detallados útiles para depuración; desactiva para builds o pruebas de rendimiento.")]
    public bool verboseLogs = false;
    public AudioClip sfxTap;
    public AudioClip sfxSwipe;
    public AudioClip sfxDrag;
    public AudioClip sfxLongPress;
    public AudioClip sfxMulti; // clip opcional para multitouch (si se deja vacío, se mezclarán los clips individuales)
    public bool layerMultiSounds = true; // si true, en multitouch se reproducen los clips individuales superpuestos
    public float pitchVariance = 0.07f; // ligera variación de pitch para hacerlo más divertido
    public bool playSfxOnDragStart = false; // no reproducir SFX en inicio de drag por defecto (sonará al soltar)

    // Estado por dedo (Enhanced Touch)
    struct TouchState
    {
        public int fingerIndex;
        public Vector2 startPos;
        public Vector2 lastPos;
        public float startTime;
        public float firstMoveTime; // tiempo en que se detectó el primer movimiento significativo
        public bool isLongPressed;
        public bool isDragging;
    }

    Dictionary<int, TouchState> states = new Dictionary<int, TouchState>();

    // Estado para mouse (simula 1 puntero independiente)
    struct PointerState
    {
        public Vector2 startPos;
        public Vector2 lastPos;
        public float startTime;
        public float firstMoveTime;
        public bool isLongPressed;
        public bool isDragging;
        public bool active;
    }

    PointerState mouseState;

    // Mensajes para overlay (solo decisiones finales por frame)
    List<string> onscreenMessages = new List<string>();

    List<EndedEvent> endedEvents = new List<EndedEvent>();

    // Evento público para que visualizadores u otros componentes puedan reaccionar
    public static EndedEventDelegate OnInputEnded;
    public static MultiTouchDelegate OnMultiTouch;
    // Drag lifecycle events
    public static DragStartDelegate OnDragStart;
    public static DragMoveDelegate OnDragMove;
    public static DragEndDelegate OnDragEnd;

    private void Awake()
    {
        // Asegurarnos de tener un AudioSource si hay clips a reproducir
        if (sfxSource == null)
        {
            var go = new GameObject("TouchDebugger_SFX");
            go.transform.SetParent(transform);
            sfxSource = go.AddComponent<AudioSource>();
            sfxSource.playOnAwake = false;
            sfxSource.volume = 1f;
            sfxSource.spatialBlend = 0f; // 2D sound
        }

        // Validar y normalizar umbrales para evitar configuraciones absurdas desde el Inspector
        ValidateAndClampThresholds();
        if (verboseLogs) Debug.Log($"TouchDebugger thresholds: swipeMinDistance={swipeMinDistance}, swipeMaxDuration={swipeMaxDuration}, swipeSpeedThreshold={swipeSpeedThreshold}, dragHoldTime={dragHoldTime}, dragStartDistance={dragStartDistance}");
    }

    void ValidateAndClampThresholds()
    {
        // Evitar valores negativos o nulos
        if (swipeMinDistance < 5f) swipeMinDistance = 5f;
        if (swipeMaxDuration <= 0f) swipeMaxDuration = 0.35f;
        if (swipeSpeedThreshold < 50f) swipeSpeedThreshold = 300f;
        if (dragHoldTime < 0f) dragHoldTime = 0.05f;
        if (dragStartDistance < 1f) dragStartDistance = 3f;

        // Si dragStartDistance es demasiado grande respecto al tamaño de pantalla, lo reducimos
        float screenMax = Mathf.Max(Screen.width, Screen.height);
        if (dragStartDistance > screenMax * 0.5f)
        {
            float old = dragStartDistance;
            dragStartDistance = Mathf.Clamp(dragStartDistance, 3f, Mathf.Max(8f, screenMax * 0.15f));
            Debug.LogWarning($"TouchDebugger: dragStartDistance ({old}) demasiado grande para la pantalla; reducido a {dragStartDistance}");
        }
        // Si swipeMinDistance es mayor que la pantalla (probable error), reducirlo
        if (swipeMinDistance > screenMax * 0.75f)
        {
            float old = swipeMinDistance;
            swipeMinDistance = Mathf.Clamp(swipeMinDistance, 5f, Mathf.Max(80f, screenMax * 0.2f));
            Debug.LogWarning($"TouchDebugger: swipeMinDistance ({old}) demasiado grande para la pantalla; reducido a {swipeMinDistance}");
        }
    }

    private void OnEnable()
    {
        EnhancedTouchSupport.Enable();
#if UNITY_EDITOR
        if (useTouchSimulationInEditor)
            TouchSimulation.Enable();
        else
            TouchSimulation.Disable();
#endif
    }

    private void OnDisable()
    {
        EnhancedTouchSupport.Disable();
#if UNITY_EDITOR
        TouchSimulation.Disable();
#endif
    }

    void Update()
    {
        // limpiar lista de eventos terminados de frame anterior
        endedEvents.Clear();

        // Procesar toques reales (mobile / touch)
        ProcessEnhancedTouches();

        // Procesar mouse (si corresponde)
        if (enableMouseInEditor && (Application.isEditor || Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.LinuxEditor))
        {
            if (!useTouchSimulationInEditor)
                ProcessMouseAsPointer();
        }

        // Manejar los eventos que terminaron este frame (individuales o multitouch)
        HandleEndedEvents();

        // Shortcuts para probar SFX en Editor: 1=Tap, 2=Swipe, 3=Drag, 4=LongPress
#if UNITY_EDITOR
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.digit1Key.wasPressedThisFrame) PlaySFXForType(InputType.Tap);
            if (kb.digit2Key.wasPressedThisFrame) PlaySFXForType(InputType.Swipe);
            if (kb.digit3Key.wasPressedThisFrame) PlaySFXForType(InputType.Drag);
            if (kb.digit4Key.wasPressedThisFrame) PlaySFXForType(InputType.LongPress);
        }
#endif
    }

    void ProcessEnhancedTouches()
    {
        foreach (var touch in Touch.activeTouches)
        {
            int id = touch.finger.index;

            if (!states.TryGetValue(id, out var st))
            {
                // Begin
                st = new TouchState
                {
                    fingerIndex = id,
                    startPos = touch.screenPosition,
                    lastPos = touch.screenPosition,
                    startTime = Time.time,
                    firstMoveTime = -1f,
                    isLongPressed = false,
                    isDragging = false
                };
                states[id] = st;
            }

            float duration = Time.time - st.startTime;
            Vector2 currentPos = touch.screenPosition;
            Vector2 totalDelta = currentPos - st.startPos;
            Vector2 frameDelta = currentPos - st.lastPos;
            float totalDistSoFar = totalDelta.magnitude;
            float speedSoFar = totalDistSoFar / Mathf.Max(duration, 0.001f);

            // Registrar firstMoveTime cuando haya movimiento significativo desde el inicio
            if (st.firstMoveTime < 0f && totalDistSoFar > tapMaxMove)
            {
                st.firstMoveTime = Time.time;
                states[id] = st;
            }

            // Long press detection
            if (!st.isLongPressed && duration >= longPressTime && totalDelta.sqrMagnitude <= longPressMoveTolerance * longPressMoveTolerance)
            {
                st.isLongPressed = true;
                states[id] = st;
            }

            // Drag detection
            if (!st.isDragging && float.IsFinite(totalDistSoFar) && totalDistSoFar >= 0f)
            {
                if (totalDistSoFar >= dragStartDistance && duration >= dragHoldTime)
                {
                    st.isDragging = true;
                    states[id] = st;
                    OnDragStarted(id, currentPos);
                    // emit drag start event for visualizers
                    OnDragStart?.Invoke(id, currentPos);
                }
            }

            // If already dragging, emit move updates so visualizer can trace path
            if (st.isDragging)
            {
                OnDragMove?.Invoke(id, currentPos);
            }
            
            // Update lastPos
            st.lastPos = currentPos;
            states[id] = st;

            // Handle Ended phase: calcular decisión final y almacenar en endedEvents
            if (touch.phase == UnityEngine.InputSystem.TouchPhase.Ended || touch.phase == UnityEngine.InputSystem.TouchPhase.Canceled)
            {
                float totalDistance = (currentPos - st.startPos).magnitude;
                InputType type = InputType.Unknown;

                // DEBUG: show internal isDragging state to help diagnose misclassification
                if (verboseLogs) Debug.Log($"TouchDebugger: Ended (precheck) id={id} isDragging={st.isDragging} firstMoveTime={st.firstMoveTime:F2} dist={totalDistance:F1} dur={duration:F2}");

                // Guard: si fue prácticamente un tap (muy poco movimiento + corto) clasificar siempre como Tap
                if (totalDistance <= tapMaxMove && duration <= tapMaxDuration)
                {
                    type = InputType.Tap;
                    var evTap = new EndedEvent { id = id, type = type, position = currentPos, duration = duration, distance = totalDistance, startPos = st.startPos };
                    endedEvents.Add(evTap);
                    OnInputEnded?.Invoke(evTap);
                    if (verboseLogs) Debug.Log($"TouchDebugger: EarlyTapGuard id={id} -> Tap (dist={totalDistance:F1} dur={duration:F2})");
                    states.Remove(id);
                    continue; // next touch
                }

                // Calcular initialHold: tiempo entre el inicio y el primer movimiento significativo
                float initialHold = (st.firstMoveTime >= 0f) ? (st.firstMoveTime - st.startTime) : duration;

                // Nueva regla solicitada por el usuario:
                // Drag: initialHold >= initialHoldForDrag AND totalDistance >= dragMinDistance AND duration >= dragMinTotalDuration
                if (initialHold >= initialHoldForDrag && totalDistance >= dragMinDistance && duration >= dragMinTotalDuration)
                {
                    type = InputType.Drag;
                    if (verboseLogs) Debug.Log($"TouchDebugger Decision: id={id} initialHold={initialHold:F2} totalDist={totalDistance:F1} duration={duration:F2} -> Drag (initialHold>=initialHoldForDrag && totalDist>=dragMinDistance && dur>=dragMinTotalDuration)");
                }
                // Swipe (según tu definición): initialHold < initialHoldForDrag AND totalDistance < dragMinDistance AND duration < dragMinTotalDuration
                else if (initialHold < initialHoldForDrag && totalDistance < dragMinDistance && duration < dragMinTotalDuration)
                {
                    type = InputType.Swipe;
                    if (verboseLogs) Debug.Log($"TouchDebugger Decision: id={id} initialHold={initialHold:F2} totalDist={totalDistance:F1} duration={duration:F2} -> Swipe (initialHold<initialHoldForDrag && totalDist<dragMinDistance && dur<dragMinTotalDuration)");
                }
                else if (st.isLongPressed)
                    type = InputType.LongPress;
                else if (st.isDragging)
                {
                    // si se marcó dragging durante el movimiento, mantener Drag salvo que sea claramente un Tap
                    type = InputType.Drag;
                    if (verboseLogs) Debug.Log($"TouchDebugger Decision: id={id} -> Drag (was marked isDragging during movement)");
                }
                else
                {
                    // Fallback: priorizar swipe por velocidad/distancia
                    float speed = totalDistance / Mathf.Max(duration, 0.001f);
                    if (verboseLogs) Debug.Log($"TouchDebugger: Ended fallback id={id} dist={totalDistance:F1} dur={duration:F2} speed={speed:F1}");
                    if (totalDistance >= swipeMinDistance && (duration <= swipeMaxDuration || speed >= swipeSpeedThreshold))
                        type = InputType.Swipe;
                    else if (totalDistance >= dragStartDistance && duration >= dragHoldTime)
                        type = InputType.Drag;
                    else
                        type = InputType.Tap;
                    if (verboseLogs) Debug.Log($"TouchDebugger Decision: id={id} initialHold={initialHold:F2} totalDist={totalDistance:F1} duration={duration:F2} -> Fallback => {type}");
                }

                var ev = new EndedEvent { id = id, type = type, position = currentPos, duration = duration, distance = totalDistance, startPos = st.startPos };
                endedEvents.Add(ev);
                // First notify that the input ended (visualizers can inspect active drags), then notify drag end
                OnInputEnded?.Invoke(ev);
                if (ev.type == InputType.Drag)
                {
                    OnDragEnd?.Invoke(ev);
                }
                if (verboseLogs) Debug.Log($"TouchDebugger: EndedEvent added id={id} type={type} duration={duration:F2} dist={totalDistance:F1}");

                states.Remove(id);
            }
        }

        // limpiar estados huérfanos
        var keys = new List<int>(states.Keys);
        foreach (var k in keys)
        {
            bool still = false;
            foreach (var t in Touch.activeTouches)
            {
                if (t.finger.index == k) { still = true; break; }
            }
            if (!still) states.Remove(k);
        }
    }

    void ProcessMouseAsPointer()
    {
        var mouse = Mouse.current;
        if (mouse == null) return; // no mouse device available under Input System

        bool down = mouse.leftButton.wasPressedThisFrame;
        bool held = mouse.leftButton.isPressed;
        bool up = mouse.leftButton.wasReleasedThisFrame;
        Vector2 pos = mouse.position.ReadValue();

        if (down)
        {
            mouseState.active = true;
            mouseState.startPos = pos;
            mouseState.lastPos = pos;
            mouseState.startTime = Time.time;
            mouseState.isLongPressed = false;
            mouseState.isDragging = false;
            mouseState.firstMoveTime = -1f;
        }

        if (mouseState.active && held)
        {
            float duration = Time.time - mouseState.startTime;
            Vector2 totalDelta = pos - mouseState.startPos;
            float speedSoFar = totalDelta.magnitude / Mathf.Max(duration, 0.001f);

            // Registrar firstMoveTime cuando haya movimiento significativo desde el inicio
            if (mouseState.firstMoveTime < 0f && totalDelta.magnitude > tapMaxMove)
            {
                mouseState.firstMoveTime = Time.time;
            }
            
            if (!mouseState.isLongPressed && duration >= longPressTime && totalDelta.sqrMagnitude <= longPressMoveTolerance * longPressMoveTolerance)
                mouseState.isLongPressed = true;

            // Detect drag using total displacement: mark if sustained or clearly slow (avoid marking fast swipes as drags)
            if (!mouseState.isDragging && totalDelta.magnitude >= dragStartDistance && duration >= dragHoldTime)
             {
                 mouseState.isDragging = true;
                 OnDragStarted(-1, pos);
                OnDragStart?.Invoke(-1, pos);
             }

            if (mouseState.isDragging)
            {
                OnDragMove?.Invoke(-1, pos);
            }
            
            mouseState.lastPos = pos;
        }

        if (mouseState.active && up)
        {
            float duration = Time.time - mouseState.startTime;
            Vector2 totalDelta = pos - mouseState.startPos;
            float totalDist = totalDelta.magnitude;
            InputType type;

            // DEBUG: show internal isDragging state for mouse
            if (verboseLogs) Debug.Log($"TouchDebugger: Mouse Ended (precheck) isDragging={mouseState.isDragging} dist={totalDist:F1} dur={duration:F2}");

            // Guard: if virtually a tap, classify early
            if (totalDist <= tapMaxMove && duration <= tapMaxDuration)
            {
                type = InputType.Tap;
                var evTapM = new EndedEvent { id = -1, type = type, position = pos, duration = duration, distance = totalDist, startPos = mouseState.startPos };
                endedEvents.Add(evTapM);
                OnInputEnded?.Invoke(evTapM);
                if (verboseLogs) Debug.Log($"TouchDebugger: Mouse EarlyTapGuard -> Tap (dist={totalDist:F1} dur={duration:F2})");
                mouseState.active = false;
                mouseState.isDragging = false;
                return;
            }

            // Calcular initialHold para mouse
            float initialHoldMouse = (mouseState.firstMoveTime >= 0f) ? (mouseState.firstMoveTime - mouseState.startTime) : duration;

            // Aplicar las reglas solicitadas para mouse: Drag si initialHold>=initialHoldForDrag && totalDist>=dragMinDistance && duration>=dragMinTotalDuration
            if (initialHoldMouse >= initialHoldForDrag && totalDist >= dragMinDistance && duration >= dragMinTotalDuration)
            {
                type = InputType.Drag;
                if (verboseLogs) Debug.Log($"TouchDebugger Decision (mouse): initialHold={initialHoldMouse:F2} totalDist={totalDist:F1} duration={duration:F2} -> Drag");
            }
            // Swipe (regla del usuario): initialHold < initialHoldForDrag AND totalDist < dragMinDistance AND duration < dragMinTotalDuration
            else if (initialHoldMouse < initialHoldForDrag && totalDist < dragMinDistance && duration < dragMinTotalDuration)
            {
                type = InputType.Swipe;
                if (verboseLogs) Debug.Log($"TouchDebugger Decision (mouse): initialHold={initialHoldMouse:F2} totalDist={totalDist:F1} duration={duration:F2} -> Swipe");
            }
            else
            {
                if (mouseState.isLongPressed)
                    type = InputType.LongPress;
                else if (mouseState.isDragging)
                {
                    float speed = totalDist / Mathf.Max(duration, 0.001f);
                    if (totalDist <= tapMaxMove && duration <= tapMaxDuration)
                        type = InputType.Tap;
                    else if (totalDist < dragStartDistance && (totalDist >= swipeMinDistance && (duration <= swipeMaxDuration || speed >= swipeSpeedThreshold)))
                        type = InputType.Swipe;
                    else
                        type = InputType.Drag;
                }
                else
                {
                    float speed = totalDist / Mathf.Max(duration, 0.001f);
                    if (verboseLogs) Debug.Log($"TouchDebugger: Mouse Ended dist={totalDist:F1} dur={duration:F2} speed={speed:F1}");
                    if (totalDist >= swipeMinDistance && (duration <= swipeMaxDuration || speed >= swipeSpeedThreshold))
                        type = InputType.Swipe;
                    else if (totalDist >= dragStartDistance && duration >= dragHoldTime)
                        type = InputType.Drag;
                    else
                        type = InputType.Tap;
                }
                if (verboseLogs) Debug.Log($"TouchDebugger Decision (mouse final): initialHold={initialHoldMouse:F2} totalDist={totalDist:F1} duration={duration:F2} -> {type}");
             }

            // id = -1 for mouse
            var evm = new EndedEvent { id = -1, type = type, position = pos, duration = duration, distance = totalDist, startPos = mouseState.startPos };
            endedEvents.Add(evm);
            // Notify input ended first so visualizers still have the active drag, then the drag end
            OnInputEnded?.Invoke(evm);
            if (evm.type == InputType.Drag)
            {
                OnDragEnd?.Invoke(evm);
            }
            if (verboseLogs) Debug.Log($"TouchDebugger: EndedEvent added id=-1 type={type} duration={duration:F2} dist={totalDist:F1}");

             mouseState.active = false;
         }
    }

    void HandleEndedEvents()
    {
        if (endedEvents.Count == 0) return;

        // Corrección final: re-evaluar eventos marcados como Drag para evitar falsos positivos
        for (int i = 0; i < endedEvents.Count; i++)
        {
            var ev = endedEvents[i];
            // sanity
            if (ev.duration <= 0f) continue;
            float speed = ev.distance / Mathf.Max(ev.duration, 0.001f);

            if (ev.type == InputType.Drag)
            {
                // Si la distancia es mínima y el tiempo corto => Tap
                if (ev.distance <= tapMaxMove && ev.duration <= tapMaxDuration)
                {
                    ev.type = InputType.Tap;
                    endedEvents[i] = ev;
                    continue;
                }
                // Si la distancia es 0 (no movimiento) lo convertimos a Tap
                if (ev.distance <= 0.5f)
                {
                    ev.type = InputType.Tap;
                    endedEvents[i] = ev;
                    continue;
                }
            }
        }

        if (endedEvents.Count == 1)
        {
            var e = endedEvents[0];
            string label = e.type.ToString();
            LogFinal(label);
            PlaySFXForType(e.type);
        }
        else
        {
             // multitouch: agrupar por tipo
             var counts = new Dictionary<InputType, int>();
             foreach (var ev in endedEvents)
             {
                 if (!counts.ContainsKey(ev.type)) counts[ev.type] = 0;
                 counts[ev.type]++;
             }

             // Emitir evento para visualizadores con la lista de eventos de multitouch
             OnMultiTouch?.Invoke(new List<EndedEvent>(endedEvents));

             // construir mensaje como: "Multitouch: 2xTap,1xSwipe"
             var parts = new List<string>();
             foreach (var kv in counts)
                 parts.Add($"{kv.Value}x{kv.Key}");

             string msg = "Multitouch: " + string.Join(",", parts);
             LogFinal(msg);

             // reproducir SFX: si hay sfxMulti lo usamos; si no y layerMultiSounds==true mezclamos los clips individuales
             if (sfxMulti != null)
             {
                 PlayClip(sfxMulti);
             }
             else if (layerMultiSounds)
             {
                 for (int i = 0; i < counts.Count; i++)
                 {
                     // play only one of each type to reduce audio overlap cost in heavy multitouch scenarios
                     var kv = new List<KeyValuePair<InputType,int>>(counts)[i];
                     PlaySFXForType(kv.Key);
                 }
             }
         }

        // mantener un histórico corto en pantalla
        // ya lo insertamos dentro de LogFinal
    }

    void PlaySFXForType(InputType t)
    {
        if (sfxSource == null) return;
        AudioClip clip = null;
        switch (t)
        {
            case InputType.Tap: clip = sfxTap; break;
            case InputType.Swipe: clip = sfxSwipe; break;
            case InputType.Drag: clip = sfxDrag; break;
            case InputType.LongPress: clip = sfxLongPress != null ? sfxLongPress : sfxTap; break;
            default: clip = sfxTap; break;
        }
        // Fallbacks: si no hay clip específico, intentar otros para garantizar audio
        if (clip == null)
        {
            if (t == InputType.Drag && sfxSwipe != null) clip = sfxSwipe;
            else if (sfxTap != null) clip = sfxTap;
        }
        if (clip == null)
        {
            Debug.LogWarning($"TouchDebugger: no SFX asignado para {t} y no hay fallback");
            return;
        }
        PlayClip(clip);
    }

    void PlayClip(AudioClip clip)
    {
        if (sfxSource == null || clip == null) return;
        float pitch = 1f + Random.Range(-pitchVariance, pitchVariance);
        //        float originalPitch = sfxSource.pitch;
        //        sfxSource.pitch = 1f + Random.Range(-pitchVariance, pitchVariance);
        //        sfxSource.PlayOneShot(clip);
        //        sfxSource.pitch = originalPitch;
        // Log para depuración de audio
        if (verboseLogs) Debug.Log($"TouchDebugger: PlayClip {clip.name} pitch={pitch:F2}");
        sfxSource.pitch = pitch;
        sfxSource.PlayOneShot(clip);
        sfxSource.pitch = 1f;
    }

    void LogFinal(string msg)
    {
        Debug.Log(msg);
        if (!showOnscreenLog) return;
        string timed = $"{Time.time:F2} - {msg}";
        onscreenMessages.Insert(0, timed);
        if (onscreenMessages.Count > maxOnscreenMessages)
            onscreenMessages.RemoveAt(onscreenMessages.Count - 1);
    }

    void OnGUI()
    {
        if (!showOnscreenLog && !showTouchIndicators) return;

        int w = Screen.width;
        int h = 20;
        GUIStyle style = new GUIStyle(GUI.skin.label);
        style.fontSize = 14;
        style.normal.textColor = Color.white;

        if (showOnscreenLog)
        {
            GUILayout.BeginArea(new Rect(10, 10, w * 0.5f, (maxOnscreenMessages + 1) * h));
            GUILayout.BeginVertical("box");
            GUILayout.Label("TouchDebugger (overlay)", style);
            foreach (var m in onscreenMessages)
            {
                GUILayout.Label(m, style);
            }
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        if (showTouchIndicators)
        {
            GUIStyle boxStyle = new GUIStyle(GUI.skin.box);
            boxStyle.alignment = TextAnchor.MiddleCenter;

            foreach (var t in Touch.activeTouches)
            {
                Vector2 pos = t.screenPosition;
                float guiY = Screen.height - pos.y;
                Rect r = new Rect(pos.x - 20f, guiY - 20f, 40f, 40f);
                GUI.Box(r, t.finger.index.ToString(), boxStyle);

                // mostrar estado (Drag / Long) junto al indicador
                if (states.TryGetValue(t.finger.index, out var st))
                {
                    string status = (st.isDragging ? "D" : "") + (st.isLongPressed ? "L" : "");
                    var labelRect = new Rect(r.x, r.y - 18f, 60f, 16f);
                    GUI.Label(labelRect, $"{t.finger.index}:{status}", style);
                }
            }

#if UNITY_EDITOR
            if (mouseState.active)
            {
                Vector2 mpos = mouseState.lastPos;
                float guiY = Screen.height - mpos.y;
                Rect rm = new Rect(mpos.x - 15f, guiY - 15f, 30f, 30f);
                GUI.Box(rm, "M", boxStyle);

                // mostrar estado del mouse
                string mstatus = (mouseState.isDragging ? "D" : "") + (mouseState.isLongPressed ? "L" : "");
                var ml = new Rect(rm.x, rm.y - 18f, 80f, 16f);
                GUI.Label(ml, $"Mouse:{mstatus}", style);
            }
#endif
        }
    }

    void OnDragStarted(int id, Vector2 position)
    {
        // Detailed debug info for why drag started
        float dur = 0f;
        float dist = 0f;
        float speed = 0f;
        if (id >= 0)
        {
            if (states.TryGetValue(id, out var st))
            {
                dur = Time.time - st.startTime;
                dist = (position - st.startPos).magnitude;
                speed = dist / Mathf.Max(dur, 0.001f);
            }
        }
        else
        {
            dur = Time.time - mouseState.startTime;
            dist = (position - mouseState.startPos).magnitude;
            speed = dist / Mathf.Max(dur, 0.001f);
        }
        Debug.Log($"[Drag] Started id={id} pos={position} dur={dur:F2} dist={dist:F1} speed={speed:F1} thresholds: dragStartDistance={dragStartDistance} dragHoldTime={dragHoldTime} swipeSpeedThreshold={swipeSpeedThreshold}");
        // No reproducir SFX aquí por defecto; la reproducción se hace en HandleEndedEvents según la clasificación
    }
}
