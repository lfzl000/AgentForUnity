using UnityEngine;

public class HelloWorld : MonoBehaviour
{
    private void Start()
    {
        for (var i = 0; i < 5; i++)
        {
            Debug.Log("Hello, World!");
        }
    }
}
