using UnityEngine;

public class DoorInteractable : MonoBehaviour, IInteractable
{
    [SerializeField] private Transform doorPivot;
    [SerializeField] private float openAngle = 90f;
    [SerializeField] private float openSpeed = 6f;
    [SerializeField] private string prompt = "Open door";

    private Quaternion closedRotation;
    private Quaternion openRotation;
    private bool isOpen;

    public string InteractionPrompt => prompt;

    private void Awake()
    {
        if (doorPivot == null)
        {
            doorPivot = transform;
        }

        closedRotation = doorPivot.localRotation;
        openRotation = closedRotation * Quaternion.Euler(0f, openAngle, 0f);
    }

    private void Update()
    {
        Quaternion target = isOpen ? openRotation : closedRotation;
        doorPivot.localRotation = Quaternion.Slerp(doorPivot.localRotation, target, openSpeed * Time.deltaTime);
    }

    public void Interact(GameObject interactor)
    {
        isOpen = !isOpen;
    }
}
