using UnityEngine;
using UnityEngine.InputSystem;

// Muestra FPS en pantalla similar al overlay del TouchDebugger.
// - Ajusta `updateInterval` para la frecuencia de actualización.
// - Presiona la tecla F para ocultar/mostrar el contador en tiempo de ejecución.
public class FPSDisplay : MonoBehaviour
{
    [Header("Opciones")]
    public bool show = true;
    public bool showInEditor = true; // si false, no aparece cuando se está en el Editor
    [Tooltip("Segundos entre actualizaciones del número mostrado")]
    public float updateInterval = 0.5f;
    [Tooltip("Decimales mostrados en el FPS")]
    public int decimals = 1;

    [Header("Posición (pixels desde esquina superior izquierda)")]
    public Vector2 anchor = new Vector2(10f, 10f);

    [Header("Estilo (opcional)")]
    public Font font;
    public int fontSize = 14;
    public Color fontColor = Color.white;

    // Estado interno
    float timeLeft;
    float accum = 0f; // acumulador de FPS
    int frames = 0;
    string displayText = "";

    void Awake()
    {
        timeLeft = updateInterval;
        if (updateInterval <= 0f) updateInterval = 0.5f;
        // Try to favor 60 FPS on mobile builds: disable vSync and set target frame rate.
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;
    }

    void Update()
    {
        // Toggle con tecla F — usar Input System si está disponible
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.fKey.wasPressedThisFrame) show = !show;
        }
        else
        {
            // fallback al input clásico si está presente
            try
            {
                if (UnityEngine.Input.GetKeyDown(KeyCode.F)) show = !show;
            }
            catch (System.InvalidOperationException)
            {
                // Si el proyecto está configurado para usar solo el Input System, llamar aquí a Input.GetKeyDown lanza
                // InvalidOperationException; lo ignoramos porque ya intentamos Keyboard.current.
            }
        }

        if (!show) return;
        if (!showInEditor && Application.isEditor) return;

        // Usamos un acumulador de FPS basado en Time.unscaledDeltaTime
        timeLeft -= Time.unscaledDeltaTime;
        if (Time.unscaledDeltaTime > 0f)
            accum += 1f / Time.unscaledDeltaTime;
        frames++;

        if (timeLeft <= 0.0f)
        {
            float fps = (frames > 0) ? (accum / frames) : 0f;
            float ms = (fps > 0f) ? 1000f / fps : 0f;
            // Construir el texto sin usar string.Format con llaves (evita warnings del analizador)
            displayText = fps.ToString("F" + decimals) + " FPS (" + ms.ToString("F1") + " ms)";
            // reset
            timeLeft = updateInterval;
            accum = 0f;
            frames = 0;
        }
    }

    void OnGUI()
    {
        if (!show) return;
        if (!showInEditor && Application.isEditor) return;

        GUIStyle style = new GUIStyle(GUI.skin.label);
        style.fontSize = fontSize;
        style.normal.textColor = fontColor;
        if (font != null) style.font = font;

        Rect r = new Rect(anchor.x, anchor.y, 240f, 40f);
        GUI.Label(r, displayText, style);
    }
}
