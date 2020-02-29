using UnityEngine;

public class SmoothMouseLook : MonoBehaviour
{
	Vector2 _mouseAbsolute;
	Vector2 _smoothMouse;

	bool IsControlled = true;
	public Vector2 clampInDegrees = new Vector2 (360, 176);
	float speed = 50f;
	
	void Start ()
	{
		if (GetComponent<Rigidbody>()) {
			GetComponent<Rigidbody>().freezeRotation = true;
		}
	}

	void Update ()
	{
		if (Input.GetKeyDown(KeyCode.Escape)) {
			IsControlled = !IsControlled;
		}

		if (Input.GetKey(KeyCode.W)) {
			transform.position += transform.forward * Time.deltaTime * speed;
		}
		if (Input.GetKey(KeyCode.S)) {
			transform.position -= transform.forward * Time.deltaTime * speed;
		}
		if (Input.GetKey(KeyCode.A)) {
			transform.position -= transform.right * Time.deltaTime * speed;
		}
		if (Input.GetKey(KeyCode.D)) {
			transform.position += transform.right * Time.deltaTime * speed;
		}

		Vector2 sensitivity;
		Vector2 smoothing;
		bool isSmoothing;

		sensitivity = new Vector2(0.5f, 0.5f);
		smoothing = new Vector2(0.03f, 0.03f);
		isSmoothing = true;

		Quaternion targetOrientation = Quaternion.identity;
		Vector2 mouseDelta = new Vector2 (Input.GetAxisRaw ("Mouse X"), Input.GetAxisRaw ("Mouse Y"));
		if (!IsControlled) {
			mouseDelta = Vector2.zero;
		}
		if (isSmoothing) {
			mouseDelta = Vector2.Scale(mouseDelta, sensitivity);
			_smoothMouse.x = Mathf.Lerp(_smoothMouse.x, mouseDelta.x, Time.deltaTime / smoothing.x);
			_smoothMouse.y = Mathf.Lerp(_smoothMouse.y, mouseDelta.y, Time.deltaTime / smoothing.y);
			_mouseAbsolute += _smoothMouse;
		} else {
			_mouseAbsolute += Vector2.Scale(mouseDelta, sensitivity);
		}
		if (clampInDegrees.y < 360)
			_mouseAbsolute.y = Mathf.Clamp (_mouseAbsolute.y, -clampInDegrees.y * 0.5f, clampInDegrees.y * 0.5f);
		var xRotation = Quaternion.AngleAxis (-_mouseAbsolute.y, targetOrientation * Vector3.right);
		transform.localRotation = xRotation * targetOrientation;
		var yRotation = Quaternion.AngleAxis (_mouseAbsolute.x, transform.InverseTransformDirection (Vector3.up));
		transform.localRotation *= yRotation;
	}
}
