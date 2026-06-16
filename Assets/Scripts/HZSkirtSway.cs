using UnityEngine;

// Mirrors demo_cloth_skirt.c drive_skirt(): x = amplitude * sin(2*pi*frequency*t)
public class HZSkirtSway : MonoBehaviour
{
    public float amplitude = 0.2f;
    public float frequency = 1.2f;

    Vector3 basePos;

    void Start()
    {
        basePos = transform.position;
    }

    void Update()
    {
        var p = basePos;
        p.x += amplitude * Mathf.Sin(Time.time * 2.0f * Mathf.PI * frequency);
        transform.position = p;
    }
}
