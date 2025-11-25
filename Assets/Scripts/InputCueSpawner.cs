using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Generador de "cues" de input para testing.
// Cada intervalo aleatorio entre minInterval y maxInterval genera uno de los inputs:
// Tap, Swipe, Drag o MultiTouch (combinado). Dispara los eventos públicos de TouchDebugger
// para que el TouchVisualizer muestre los visuales que ya implementamos.
public class InputCueSpawner : MonoBehaviour
{
    [Header("Intervalo (segundos)")]
    public float minInterval = 1f;
    public float maxInterval = 5f;

    [Header("Opciones")]
    public bool startOnAwake = true;
    [Tooltip("Probabilidad (0-1) de generar MultiTouch en lugar de un único input")]
    [Range(0f, 1f)]
    public float multiTouchProbability = 0.15f;

    int nextId = 1000;

    void Start()
    {
        if (startOnAwake)
            StartCoroutine(SpawnLoop());
    }

    public void StartSpawning()
    {
        StopAllCoroutines();
        StartCoroutine(SpawnLoop());
    }

    public void StopSpawning()
    {
        StopAllCoroutines();
    }

    IEnumerator SpawnLoop()
    {
        while (true)
        {
            float wait = Random.Range(minInterval, maxInterval);
            yield return new WaitForSeconds(wait);
            GenerateCue();
        }
    }

    void GenerateCue()
    {
        // decide si multi-touch
        if (Random.value < multiTouchProbability)
        {
            GenerateMultiTouchCue();
            return;
        }

        // elegir tipo simple
        var types = new TouchDebuggerSimpleType[] { TouchDebuggerSimpleType.Tap, TouchDebuggerSimpleType.Swipe, TouchDebuggerSimpleType.Drag };
        TouchDebuggerSimpleType t = types[Random.Range(0, types.Length)];
        switch (t)
        {
            case TouchDebuggerSimpleType.Tap:
                EmitTap();
                break;
            case TouchDebuggerSimpleType.Swipe:
                EmitSwipe();
                break;
            case TouchDebuggerSimpleType.Drag:
                EmitDrag();
                break;
        }
    }

    enum TouchDebuggerSimpleType { Tap, Swipe, Drag }

    Vector2 RandomScreenPoint(float margin = 60f)
    {
        return new Vector2(Random.Range(margin, Screen.width - margin), Random.Range(margin, Screen.height - margin));
    }

    void EmitTap()
    {
        Vector2 p = RandomScreenPoint();
        EndedEvent ev = new EndedEvent
        {
            id = nextId++,
            type = InputType.Tap,
            position = p,
            startPos = p,
            duration = Random.Range(0.02f, 0.12f),
            distance = 0f
        };
        Debug.Log($"InputCueSpawner: Emit Tap at {p}");
        TouchDebugger.OnInputEnded?.Invoke(ev);
    }

    void EmitSwipe()
    {
        Vector2 a = RandomScreenPoint();
        // pequeño desplazamiento o moderado según tu definición; lo hacemos entre 30 y 220 px
        float ang = Random.Range(0f, Mathf.PI * 2f);
        float dist = Random.Range(30f, Mathf.Max(80f, Screen.height * 0.3f));
        Vector2 b = a + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * dist;
        b.x = Mathf.Clamp(b.x, 10f, Screen.width - 10f);
        b.y = Mathf.Clamp(b.y, 10f, Screen.height - 10f);

        EndedEvent ev = new EndedEvent
        {
            id = nextId++,
            type = InputType.Swipe,
            startPos = a,
            position = b,
            distance = (b - a).magnitude,
            duration = Random.Range(0.05f, 0.45f)
        };
        Debug.Log($"InputCueSpawner: Emit Swipe from {a} to {b} (dist={ev.distance:F1})");
        TouchDebugger.OnInputEnded?.Invoke(ev);
    }

    void EmitDrag()
    {
        Vector2 a = RandomScreenPoint();
        float ang = Random.Range(0f, Mathf.PI * 2f);
        // drag debe ser largo y sostenido: distancia >= 100
        float dist = Random.Range(120f, Mathf.Max(200f, Screen.height * 0.6f));
        Vector2 b = a + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * dist;
        b.x = Mathf.Clamp(b.x, 10f, Screen.width - 10f);
        b.y = Mathf.Clamp(b.y, 10f, Screen.height - 10f);

        EndedEvent ev = new EndedEvent
        {
            id = nextId++,
            type = InputType.Drag,
            startPos = a,
            position = b,
            distance = (b - a).magnitude,
            duration = Random.Range(0.6f, 1.6f)
        };
        Debug.Log($"InputCueSpawner: Emit Drag from {a} to {b} (dist={ev.distance:F1}, dur={ev.duration:F2})");
        TouchDebugger.OnInputEnded?.Invoke(ev);
    }

    void GenerateMultiTouchCue()
    {
        // generar entre 2 y 4 eventos simultáneos, tipos aleatorios
        int n = Random.Range(2, 5);
        var list = new List<EndedEvent>();
        for (int i = 0; i < n; i++)
        {
            // elegir entre Tap/Swipe/Drag con pesos
            float r = Random.value;
            if (r < 0.5f) // Tap
            {
                Vector2 p = RandomScreenPoint();
                list.Add(new EndedEvent { id = nextId++, type = InputType.Tap, position = p, startPos = p, duration = Random.Range(0.02f, 0.15f), distance = 0f });
            }
            else if (r < 0.85f) // Swipe
            {
                Vector2 a = RandomScreenPoint();
                float ang = Random.Range(0f, Mathf.PI * 2f);
                float dist = Random.Range(40f, 200f);
                Vector2 b = a + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * dist;
                b.x = Mathf.Clamp(b.x, 10f, Screen.width - 10f);
                b.y = Mathf.Clamp(b.y, 10f, Screen.height - 10f);
                list.Add(new EndedEvent { id = nextId++, type = InputType.Swipe, startPos = a, position = b, distance = (b - a).magnitude, duration = Random.Range(0.05f, 0.4f) });
            }
            else // Drag
            {
                Vector2 a = RandomScreenPoint();
                float ang = Random.Range(0f, Mathf.PI * 2f);
                float dist = Random.Range(100f, 300f);
                Vector2 b = a + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * dist;
                b.x = Mathf.Clamp(b.x, 10f, Screen.width - 10f);
                b.y = Mathf.Clamp(b.y, 10f, Screen.height - 10f);
                list.Add(new EndedEvent { id = nextId++, type = InputType.Drag, startPos = a, position = b, distance = (b - a).magnitude, duration = Random.Range(0.6f, 1.6f) });
            }
        }

        Debug.Log($"InputCueSpawner: Emit MultiTouch with {list.Count} events");
        TouchDebugger.OnMultiTouch?.Invoke(list);
    }
}

