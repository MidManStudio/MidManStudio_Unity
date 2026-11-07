// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/samples.md, section "AutoRefTest.cs"
// ============================================================================
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.AutoReference;
/// <summary>
/// Empty component for trying Auto Reference in the Inspector (Auto Ref sample, OtherStuff scene). It has three
/// reference fields of different kinds, a private serialized <c>CapsuleCollider</c>, a public <c>Rigidbody</c> and a
/// public <c>MeshCollider</c>. <c>[MID_AutoRefable(true)]</c> opts the class into resolving and makes the editor add
/// a <c>MID_AutoRef</c> component next to it. Start and Update are empty.
/// </summary>
[MID_AutoRefable(true)]
public class AutoRefTest : MonoBehaviour
{

    [SerializeField] CapsuleCollider _collider;
    public Rigidbody _rb;
    public MeshCollider _mesh;


    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
