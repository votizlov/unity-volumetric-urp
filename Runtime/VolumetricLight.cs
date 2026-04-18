using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Light))]
public sealed class VolumetricLight : MonoBehaviour
{
    public bool processVolumetricLighting = true;

    public bool ShouldProcess => isActiveAndEnabled && processVolumetricLighting;
}
