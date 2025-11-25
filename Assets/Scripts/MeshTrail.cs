using System.Collections.Generic;
using UnityEngine;

// Mesh-based trail: creates a quad-strip mesh from points in XY plane.
// Usage: Init(width, maxPoints), AddPoint(worldPos), Clear(), SetColor(color)
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class MeshTrail : MonoBehaviour
{
    Mesh _mesh;
    MeshFilter _mf;
    MeshRenderer _mr;
    List<Vector3> _points = new List<Vector3>();
    List<Vector3> _verts = new List<Vector3>();
    List<int> _tris = new List<int>();
    List<Vector2> _uvs = new List<Vector2>();

    public float width = 0.06f;
    public int maxPoints = 200;
    public float minSqrDistance = 4f; // px^2 in world units approx (tweak)

    void Awake()
    {
        _mf = GetComponent<MeshFilter>();
        _mr = GetComponent<MeshRenderer>();
        _mesh = new Mesh();
        _mesh.name = "MeshTrailMesh";
        _mesh.MarkDynamic();
        _mf.mesh = _mesh;
    }

    public void Init(float w, int maxPointsAllowed, float minSqrDist = 4f)
    {
        width = w;
        maxPoints = Mathf.Max(2, maxPointsAllowed);
        minSqrDistance = Mathf.Max(0.0001f, minSqrDist);
        Clear();
    }

    public void SetMaterial(Material mat)
    {
        if (_mr != null && mat != null)
            _mr.sharedMaterial = mat;
    }

    public void SetColor(Color c)
    {
        if (_mr != null && _mr.sharedMaterial != null)
        {
            try
            {
                _mr.sharedMaterial.color = c;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"MeshTrail: could not set material color: {ex.Message}");
            }
        }
    }

    public void AddPoint(Vector3 wp)
    {
        if (_points.Count > 0)
        {
            float d = (_points[_points.Count - 1] - wp).sqrMagnitude;
            if (d < minSqrDistance) return;
        }

        if (_points.Count >= maxPoints)
        {
            // remove oldest
            _points.RemoveAt(0);
        }
        _points.Add(wp);
        UpdateMesh();
    }

    void UpdateMesh()
    {
        if (_points.Count < 2)
        {
            _mesh.Clear();
            return;
        }

        int n = _points.Count;
        _verts.Clear();
        _tris.Clear();
        _uvs.Clear();

        // Build quad strip
        for (int i = 0; i < n; i++)
        {
            Vector3 p = _points[i];
            Vector3 dir;
            if (i == n - 1) dir = p - _points[i - 1];
            else dir = _points[Mathf.Min(i + 1, n - 1)] - p;
            dir.z = 0f;
            if (dir.sqrMagnitude <= 0.000001f) dir = Vector3.right;
            dir.Normalize();
            Vector3 perp = new Vector3(-dir.y, dir.x, 0f);
            Vector3 v1 = p + perp * (width * 0.5f);
            Vector3 v2 = p - perp * (width * 0.5f);
            _verts.Add(v1);
            _verts.Add(v2);

            float u = (float)i / Mathf.Max(1, n - 1);
            _uvs.Add(new Vector2(u, 0f));
            _uvs.Add(new Vector2(u, 1f));
        }

        for (int i = 0; i < n - 1; i++)
        {
            int idx = i * 2;
            // tri 1
            _tris.Add(idx);
            _tris.Add(idx + 2);
            _tris.Add(idx + 1);
            // tri 2
            _tris.Add(idx + 1);
            _tris.Add(idx + 2);
            _tris.Add(idx + 3);
        }

        _mesh.Clear();
        _mesh.SetVertices(_verts);
        _mesh.SetTriangles(_tris, 0);
        _mesh.SetUVs(0, _uvs);
        _mesh.RecalculateBounds();
        // optional: normals aren't strictly needed for Unlit shader
    }

    public void Clear()
    {
        _points.Clear();
        _mesh.Clear();
    }
}
