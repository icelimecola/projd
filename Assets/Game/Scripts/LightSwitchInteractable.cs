using UnityEngine;

public class LightSwitchInteractable : MonoBehaviour, IInteractable
{
    [SerializeField] private Light targetLight;
    [SerializeField] private string prompt = "Toggle light";

    public string InteractionPrompt => prompt;

    public void Interact(GameObject interactor)
    {
        if (targetLight == null)
        {
            return;
        }

        targetLight.enabled = !targetLight.enabled;
    }
}
