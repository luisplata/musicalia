using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Visualizador simple para mostrar en pantalla los inputs detectados por TouchDebugger
// - Asigna en el Inspector: circleSprite (sprite circular), linePrefab (opcional, LineRenderer prefab)
// - El visualizador crea UI Images en un Canvas ScreenSpace-Overlay para indicadores y usa LineRenderer (o UI image linea) para drags
public class TouchVisualizer : MonoBehaviour
{
    [Header("Assets (assign in Editor)")]
    public Sprite circleSprite; // sprite circular que el usuario colocará
    [Tooltip("Assign a LineRenderer from the scene to use as a template (it will be cloned at runtime). If empty, UI fallback will be used.")]
    public LineRenderer lineTemplate; // opcional: LineRenderer presente en escena que se utilizará como plantilla

    [Header("Visuals")]
    public Color tapColor = new Color(0.2f, 0.9f, 0.2f, 1f);
    public Color swipeColor = new Color(0.2f, 0.6f, 1f, 1f);
    public Color dragColor = new Color(1f, 0.45f, 0.2f, 1f);
    public Color multiColor = new Color(1f, 0.85f, 0.2f, 1f);

    // Line style options
    [Header("Line Styles")]
    [Tooltip("If true, always draw lines as UI elements (Canvas). If false, try scene LineRenderer/template or runtime LineRenderer.")]
    public bool preferUIForLines;

    [Header("World-space line widths (units)")]
    public float tapLineWorldWidth = 0.04f;
    public float swipeLineWorldWidth = 0.04f;
    public float dragLineWorldWidth = 0.06f;
    public float multiLineWorldWidth = 0.05f;

    [Header("UI line thickness (pixels)")]
    public float tapLineUIThickness = 4f;
    public float swipeLineUIThickness = 3f;
    public float dragLineUIThickness = 6f;
    public float multiLineUIThickness = 5f;

    public float circleSize = 48f; // px
    public float swipeSize = 36f;
    public float lifeTime = 0.9f;
    public float lineThickness = 6f; // px for UI fallback

    [Header("Performance")]
    [Tooltip("Pool initial size for circle UI indicators")]
    public int circlePoolSize;
    [Tooltip("Pool initial size for runtime LineRenderers")]
    public int lrPoolSize;
    [Tooltip("Pool initial size for MeshTrail objects (recommended for drags)")]
    public int meshTrailPoolSize;

    [Tooltip("Minimum squared screen distance (px^2) between spawned trail dots to reduce spawn rate")]
    public float dragDotMinSqrDistance; // ~8px
    [Tooltip("Minimum seconds between spawned trail dots per drag")]
    public float dragDotMinInterval;
    [Tooltip("Max points stored per drag to avoid runaway memory usage")]
    public int dragMaxPoints = 300;

    [Header("Debug")]
    public bool verboseLogs;

    // runtime fields
    Queue<GameObject> _circlePool = new Queue<GameObject>();
    Material _sharedLineMaterial;
    Queue<GameObject> _lrPool = new Queue<GameObject>();
    Queue<GameObject> _meshTrailPool = new Queue<GameObject>();

    Camera _mainCamera;
    Canvas _canvas;
    RectTransform _canvasRect;

    // Active drag visuals by id
    class DragVisual
    {
        public GameObject Go; // for LineRenderer or parent for UI trail
        public LineRenderer Lr; // if using world-space LineRenderer
        public MeshTrail Mt; // mesh-based trail
        public List<Vector3> Points = new List<Vector3>();
        public Vector2 LastDotPos;
        public float LastDotTime;
    }
    Dictionary<int, DragVisual> _activeDrags = new Dictionary<int, DragVisual>();
    // Evitar que se dibuje la línea final si el drag fue trazado en tiempo real.
    // Guardamos ids recientemente terminados con un TTL corto.
    Dictionary<int, float> _recentlyEndedDrags = new Dictionary<int, float>();

    void Awake()
    {
        _mainCamera = Camera.main;
        // Ensure there's a Canvas (Screen Space - Overlay)
        // Try FindAnyObjectByType (fast newer API) and fallback to FindObjectOfType
        // Try newer fast APIs first, fallback safely
        _canvas = null;
        try
        {
            _canvas = Object.FindAnyObjectByType<Canvas>();
        }
        catch (System.Exception ex) { if (verboseLogs) Debug.LogWarning($"TouchVisualizer: FindAnyObjectByType failed: {ex.Message}"); }
        if (_canvas == null)
        {
            try { _canvas = Object.FindFirstObjectByType<Canvas>(); } catch (System.Exception ex) { if (verboseLogs) Debug.LogWarning($"TouchVisualizer: FindFirstObjectByType failed: {ex.Message}"); }
        }
        // avoid using obsolete FindObjectOfType; if previous lookups failed try FindFirstObjectByType again
        if (_canvas == null)
        {
            try { _canvas = Object.FindFirstObjectByType<Canvas>(); } catch (System.Exception ex) { if (verboseLogs) Debug.LogWarning($"TouchVisualizer: final FindFirstObjectByType failed: {ex.Message}"); }
        }

        if (_canvas == null || _canvas.renderMode != RenderMode.ScreenSpaceOverlay)
        {
            var go = new GameObject("TouchVisualizer_Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            _canvas = go.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.GetComponent<CanvasScaler>();
            if (scaler != null) scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _canvasRect = _canvas.GetComponent<RectTransform>();
        }
        else
        {
            _canvasRect = _canvas.GetComponent<RectTransform>();
        }

        // Ensure sensible defaults if inspector left them zero
        if (circlePoolSize <= 0) circlePoolSize = 32;
        if (lrPoolSize <= 0) lrPoolSize = 6;
        if (meshTrailPoolSize <= 0) meshTrailPoolSize = 6;
        if (dragDotMinSqrDistance <= 0f) dragDotMinSqrDistance = 64f;
        if (dragDotMinInterval <= 0f) dragDotMinInterval = 0.03f;
        if (dragMaxPoints <= 0) dragMaxPoints = 300;

        // prepare pool of circle UI objects
        for (int i = 0; i < circlePoolSize; i++)
        {
            var go = CreatePooledCircle();
            go.SetActive(false);
            _circlePool.Enqueue(go);
        }

        // prepare pool of LineRenderer objects
        for (int i = 0; i < lrPoolSize; i++)
        {
            var go = CreatePooledLineRenderer();
            go.SetActive(false);
            _lrPool.Enqueue(go);
        }

        // prepare pool of MeshTrail objects
        for (int i = 0; i < meshTrailPoolSize; i++)
        {
            var mt = CreatePooledMeshTrail();
            mt.SetActive(false);
            _meshTrailPool.Enqueue(mt);
        }

        // prepare shared LineRenderer material (fallbacks)
        Shader s = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Universal Render Pipeline/Unlit");
        if (s != null)
        {
            _sharedLineMaterial = new Material(s);
            // don't set color here; use LineRenderer.startColor/endColor
            // assign shared material to already pooled objects to avoid duplicates
            foreach (var go in _lrPool) {
                if (go == null) continue;
                var lr = go.GetComponent<LineRenderer>();
                if (lr != null) lr.material = _sharedLineMaterial;
            }
            foreach (var go in _meshTrailPool) {
                if (go == null) continue;
                var mr = go.GetComponent<MeshRenderer>();
                if (mr != null) mr.sharedMaterial = _sharedLineMaterial;
            }
        }
    }

    void OnEnable()
    {
        TouchDebugger.OnInputEnded += HandleInputEnded;
        TouchDebugger.OnMultiTouch += HandleMultiTouch;
        TouchDebugger.OnDragStart += HandleDragStart;
        TouchDebugger.OnDragMove += HandleDragMove;
        TouchDebugger.OnDragEnd += HandleDragEnd;
    }

    void OnDisable()
    {
        TouchDebugger.OnInputEnded -= HandleInputEnded;
        TouchDebugger.OnMultiTouch -= HandleMultiTouch;
        TouchDebugger.OnDragStart -= HandleDragStart;
        TouchDebugger.OnDragMove -= HandleDragMove;
        TouchDebugger.OnDragEnd -= HandleDragEnd;
    }

    void HandleDragStart(int id, Vector2 screenPos)
    {
        // create a visual representation for this drag
        if (_activeDrags.ContainsKey(id)) return;
        // marcar como activo y suprimir linea final momentáneamente
        _recentlyEndedDrags[id] = Time.time + 0.25f;
        var dv = new DragVisual();
        // If prefer UI trail, we'll spawn small circles during moves.
        // Otherwise create a MeshTrail (preferred) or fallback to LineRenderer if MeshTrail unavailable.
        if (!preferUIForLines && _mainCamera != null)
        {
            // try get a MeshTrail from pool
            GameObject mtGo = (_meshTrailPool.Count > 0) ? _meshTrailPool.Dequeue() : CreatePooledMeshTrail();
            mtGo.SetActive(true);
            mtGo.name = $"TV_Drag_{id}";
            mtGo.transform.SetParent(this.transform, true);
            var mt = mtGo.GetComponent<MeshTrail>();
            float worldW = GetWorldWidthForType(InputType.Drag);
            mt.Init(worldW, dragMaxPoints, dragDotMinSqrDistance * 0.0001f);
            mt.SetMaterial(_sharedLineMaterial);
            mt.SetColor(dragColor);
            dv.Go = mtGo;
            dv.Mt = mt;
        }
        else if (_mainCamera != null)
        {
            var goLr = GetPooledLineRenderer();
            goLr.name = $"TV_Drag_{id}";
            goLr.transform.SetParent(this.transform, true);
            goLr.SetActive(true);
            var lr = goLr.GetComponent<LineRenderer>();
            lr.positionCount = 0;
            float w = GetWorldWidthForType(InputType.Drag);
            lr.startWidth = lr.endWidth = (w > 0f) ? w : 0.06f;
            if (_sharedLineMaterial != null) lr.material = _sharedLineMaterial;
            lr.startColor = dragColor; lr.endColor = dragColor;
            dv.Lr = lr;
            dv.Go = goLr;
        }
        _activeDrags[id] = dv;
        // immediately add first point
        HandleDragMove(id, screenPos);
    }

    void HandleDragMove(int id, Vector2 screenPos)
    {
        if (!_activeDrags.TryGetValue(id, out var dv)) return;
        if (dv.Mt != null && _mainCamera != null)
        {
            float z = Mathf.Abs(_mainCamera.transform.position.z);
            Vector3 w = _mainCamera.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, z));
            w.z = 0f;
            dv.Mt.AddPoint(w);
        }
        else if (dv.Lr != null && _mainCamera != null)
        {
            float z = Mathf.Abs(_mainCamera.transform.position.z);
            Vector3 w = _mainCamera.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, z));
            w.z = 0f;
            if (dv.Points.Count < dragMaxPoints)
            {
                dv.Points.Add(w);
                dv.Lr.positionCount = dv.Points.Count;
                dv.Lr.SetPosition(dv.Points.Count - 1, w);
            }
        }
        else
        {
            // UI trail: spawn small circle images along the path (short TTL)
            float now = Time.time;
            float small = Mathf.Max(6f, circleSize * 0.25f);
            // only spawn if enough time or distance since last dot
            Vector2 last = dv.LastDotPos;
            if (dv.LastDotTime <= 0f || (now - dv.LastDotTime >= dragDotMinInterval) || (screenPos - last).sqrMagnitude >= dragDotMinSqrDistance)
            {
                SpawnCircleUI(screenPos, dragColor, small, lifeTime * 0.8f);
                dv.LastDotPos = screenPos;
                dv.LastDotTime = now;
            }
        }
    }

    void HandleDragEnd(EndedEvent ev)
    {
        if (!_activeDrags.TryGetValue(ev.id, out var dv)) return;
        // finalize: if mesh trail exists, schedule return to pool
        if (dv.Mt != null)
        {
            try { dv.Mt.SetColor(dragColor); } catch (System.Exception ex) { if (verboseLogs) Debug.LogWarning($"TouchVisualizer: SetColor failed: {ex.Message}"); }
            StartCoroutine(ReturnMeshTrailAfter(dv.Go, lifeTime));
        }
        else if (dv.Lr != null)
        {
            try { dv.Lr.startColor = dragColor; dv.Lr.endColor = dragColor; } catch (System.Exception ex) { if (verboseLogs) Debug.LogWarning($"TouchVisualizer: set LineRenderer color failed: {ex.Message}"); }
            StartCoroutine(ReturnLineAfter(dv.Go, lifeTime));
        }
        else
        {
            // UI trail items already have their own fade timers; nothing to do
            ReturnCircleToPool(dv.Go);
        }
        _activeDrags.Remove(ev.id);
        // registrar supresión breve para evitar que HandleInputEnded dibuje una línea final
        _recentlyEndedDrags[ev.id] = Time.time + 0.25f;
    }

    IEnumerator ReturnLineAfter(GameObject go, float ttl)
    {
        yield return new WaitForSeconds(ttl);
        ReturnLineToPool(go);
    }

    IEnumerator ReturnMeshTrailAfter(GameObject go, float ttl)
    {
        yield return new WaitForSeconds(ttl);
        if (go == null) yield break;
        go.SetActive(false);
        _meshTrailPool.Enqueue(go);
    }

    void HandleMultiTouch(List<EndedEvent> events)
    {
        // Draw combined marker near the average position
        if (events == null || events.Count == 0) return;
        Vector2 avg = Vector2.zero;
        foreach (var e in events) avg += e.position;
        avg /= events.Count;

        // spawn a multi circle and small circles for each event
        SpawnCircleUI(avg, multiColor, circleSize * 1.2f, lifeTime);
        float offset = 18f;
        for (int i = 0; i < events.Count; i++)
        {
            Vector2 pos = events[i].position;
            // slight offset outward from avg to visualize multiple
            Vector2 dir = (pos - avg).normalized;
            if (dir == Vector2.zero) dir = new Vector2(Mathf.Cos(i), Mathf.Sin(i)).normalized;
            Vector2 p = avg + dir * offset;
            SpawnCircleUI(p, Color.Lerp(multiColor, Color.white, 0.25f), circleSize * 0.7f, lifeTime);
        }
    }

    void HandleInputEnded(EndedEvent ev)
    {
        // limpiar expirados
        var keys = new List<int>(_recentlyEndedDrags.Keys);
        foreach (var k in keys) if (_recentlyEndedDrags[k] < Time.time) _recentlyEndedDrags.Remove(k);
        switch (ev.type)
        {
            case InputType.Tap:
                SpawnCircleUI(ev.position, tapColor, circleSize, lifeTime);
                break;
            case InputType.Swipe:
                SpawnCircleUI(ev.position, swipeColor, swipeSize, lifeTime);
                // draw short line from start to end using world or UI according to preference
                SpawnLineWorldOrUI(ev.startPos, ev.position, InputType.Swipe, swipeColor, lifeTime);
                break;
            case InputType.Drag:
                // for Drag: si ya existía un visual activo para este id (se estuvo trazando en tiempo real),
                // evitamos crear una línea adicional entre inicio y fin. Solo dibujamos el marcador final.
                SpawnCircleUI(ev.position, dragColor, circleSize * 1.1f, lifeTime);
                // No dibujar línea final entre inicio y fin (la trayectoria ya se pintó en tiempo real).
                break;
            case InputType.LongPress:
                SpawnCircleUI(ev.position, dragColor, circleSize * 0.9f, lifeTime);
                break;
            default:
                SpawnCircleUI(ev.position, Color.white, circleSize * 0.8f, lifeTime);
                break;
        }
    }

    void SpawnCircleUI(Vector2 screenPos, Color color, float sizePx, float ttl)
    {
        if (circleSprite == null)
        {
            // no sprite: nothing to spawn
            return;
        }
        var go = GetPooledCircle();
        go.SetActive(true);
        var img = go.GetComponent<Image>();
        img.sprite = circleSprite;
        img.color = color;
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(sizePx, sizePx);
        Vector2 localPoint;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screenPos, null, out localPoint);
        rt.anchoredPosition = localPoint;

        // animate fade and return to pool when finished
        StartCoroutine(FadeAndReturnToPool(img, ttl));
    }

    GameObject CreatePooledCircle()
    {
        var go = new GameObject("TV_Circle", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(_canvas.transform, false);
        var img = go.GetComponent<Image>();
        img.raycastTarget = false;
        go.SetActive(false);
        return go;
    }

    GameObject CreatePooledLineRenderer()
    {
        var go = new GameObject("TV_PooledLR");
        var lr = go.AddComponent<LineRenderer>();
        lr.positionCount = 0;
        lr.useWorldSpace = true;
        lr.numCapVertices = 6;
        lr.numCornerVertices = 4;
        lr.startWidth = lr.endWidth = 0.06f;
        if (_sharedLineMaterial != null) lr.material = _sharedLineMaterial;
        go.SetActive(false);
        return go;
    }

    GameObject CreatePooledMeshTrail()
    {
        var go = new GameObject("TV_PooledMeshTrail");
        go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        go.AddComponent<MeshTrail>();
        // setup default material if available
        if (_sharedLineMaterial != null) mr.sharedMaterial = _sharedLineMaterial;
        else
        {
            Shader s = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            if (s != null) mr.sharedMaterial = new Material(s);
        }
        go.SetActive(false);
        return go;
    }

    GameObject GetPooledCircle()
    {
        if (_circlePool.Count > 0)
        {
            return _circlePool.Dequeue();
        }
        else
        {
            return CreatePooledCircle();
        }
    }

    GameObject GetPooledLineRenderer()
    {
        if (_lrPool.Count > 0) return _lrPool.Dequeue();
        return CreatePooledLineRenderer();
    }

    void ReturnCircleToPool(GameObject go)
    {
        go.SetActive(false);
        _circlePool.Enqueue(go);
    }

    void ReturnLineToPool(GameObject go)
    {
        if (go == null) return;
        var lr = go.GetComponent<LineRenderer>();
        if (lr != null) lr.positionCount = 0;
        go.SetActive(false);
        _lrPool.Enqueue(go);
    }
    
    IEnumerator FadeAndReturnToPool(Image img, float ttl)
    {
        float elapsed = 0f;
        var col = img.color;
        Transform t = img.transform;
        while (elapsed < ttl)
        {
            elapsed += Time.deltaTime;
            float a = Mathf.Lerp(1f, 0f, elapsed / ttl);
            img.color = new Color(col.r, col.g, col.b, a);
            float s = 1f + 0.3f * (elapsed / ttl);
            t.localScale = Vector3.one * s;
            yield return null;
        }
        ReturnCircleToPool(img.gameObject);
    }

    void SpawnLineWorldOrUI(Vector2 startScreen, Vector2 endScreen, InputType type, Color color, float ttl)
    {
        // Prefer LineRenderer template (scene object cloned). If not assigned, fallback to UI thin Image
        // If user prefers UI, always use UI fallback
        if (preferUIForLines)
        {
            float uiTh = GetUIThicknessForType(type);
            SpawnUILine(startScreen, endScreen, color, ttl, uiTh);
            return;
        }

        if (_mainCamera != null)
        {
            // simpler and robust: spawn a runtime LineRenderer using pooled runtime object
            float worldW = GetWorldWidthForType(type);
            SpawnRuntimeLine(startScreen, endScreen, color, ttl, worldW);
        }
        else
        {
            // If no template provided or no camera, attempt to create a runtime LineRenderer for visibility; fallback to UI if creation fails
            if (_mainCamera != null)
            {
                float worldW = GetWorldWidthForType(type);
                SpawnRuntimeLine(startScreen, endScreen, color, ttl, worldW);
            }
            else
            {
                float uiTh = GetUIThicknessForType(type);
                SpawnUILine(startScreen, endScreen, color, ttl, uiTh);
            }
         }
    }

    // Creates a LineRenderer in runtime with sensible defaults to guarantee visibility
    void SpawnRuntimeLine(Vector2 startScreen, Vector2 endScreen, Color color, float ttl, float worldWidth)
    {
        var go = GetPooledLineRenderer();
        go.transform.SetParent(this.transform, true);
        go.SetActive(true);
        var lr = go.GetComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.startWidth = lr.endWidth = (worldWidth > 0f) ? worldWidth : 0.06f;

        float z = Mathf.Abs(_mainCamera.transform.position.z);
        Vector3 wStart = _mainCamera.ScreenToWorldPoint(new Vector3(startScreen.x, startScreen.y, z));
        Vector3 wEnd = _mainCamera.ScreenToWorldPoint(new Vector3(endScreen.x, endScreen.y, z));
        wStart.z = 0f; wEnd.z = 0f;
        lr.SetPosition(0, wStart);
        lr.SetPosition(1, wEnd);
        if (_sharedLineMaterial != null) lr.material = _sharedLineMaterial;
        lr.startColor = color; lr.endColor = color;
        try { lr.sortingOrder = 5000; } catch (System.Exception ex) { if (verboseLogs) Debug.LogWarning($"TouchVisualizer: couldn't set sortingOrder: {ex.Message}"); }
        lr.enabled = true;
        if (verboseLogs) Debug.Log($"TouchVisualizer: Spawned runtime LineRenderer (wStart={wStart}, wEnd={wEnd})");
        StartCoroutine(ReturnLineAfter(go, ttl));
    }

    void SpawnUILine(Vector2 startScreen, Vector2 endScreen, Color color, float ttl, float thickness)
    {
        // Create UI Image and stretch it between two points
        var go = new GameObject("TV_Line", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(_canvas.transform, false);
        var img = go.GetComponent<Image>();
        img.color = color;
        if (thickness > 0f) img.rectTransform.sizeDelta = new Vector2(thickness, 1f);
        else img.rectTransform.sizeDelta = Vector2.one;
        img.type = Image.Type.Sliced;
        img.fillCenter = true;
        img.fillAmount = 1f;

        // Position and rotate to match line
        var rt = go.GetComponent<RectTransform>();
        Vector2 p0, p1;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, startScreen, null, out p0);
        RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, endScreen, null, out p1);
        rt.anchoredPosition = (p0 + p1) * 0.5f;
        rt.sizeDelta = new Vector2(Vector2.Distance(p0, p1), thickness);
        rt.rotation = Quaternion.FromToRotation(Vector3.right, p1 - p0);

        // destroy after ttl
        Destroy(go, ttl);
    }

    float GetWorldWidthForType(InputType type)
    {
        switch (type)
        {
            case InputType.Tap: return tapLineWorldWidth;
            case InputType.Swipe: return swipeLineWorldWidth;
            case InputType.Drag: return dragLineWorldWidth;
            case InputType.LongPress: return multiLineWorldWidth;
            default: return 0.06f;
        }
    }

    float GetUIThicknessForType(InputType type)
    {
        switch (type)
        {
            case InputType.Tap: return tapLineUIThickness;
            case InputType.Swipe: return swipeLineUIThickness;
            case InputType.Drag: return dragLineUIThickness;
            case InputType.LongPress: return multiLineUIThickness;
            default: return 6f;
        }
    }
}
