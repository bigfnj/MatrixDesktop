// Translate the opt-in native desktop pointer bridge into the canvas events
// already consumed by Matrix and UC3D. Coordinates arrive in host client pixels.
export function createPointerForwarder(canvas, {
  PointerEvent = globalThis.PointerEvent ?? globalThis.MouseEvent,
  MouseEvent = globalThis.MouseEvent,
  WheelEvent = globalThis.WheelEvent,
} = {}) {
  let down = null, buttons = 0;
  const send = (Constructor, type, fields) => {
    if (typeof Constructor === "function") canvas.dispatchEvent(new Constructor(type, { bubbles: true, cancelable: true, ...fields }));
  };
  return data => {
    if (data == null || data.eventType === "leave") {
      down = null; buttons = 0;
      send(PointerEvent, "pointerleave", { pointerId: 1, pointerType: "mouse", isPrimary: true, buttons: 0 });
      return true;
    }
    const type = data.eventType;
    if (!["move", "down", "up", "wheel"].includes(type)) return false;
    const { x, y, viewportWidth, viewportHeight } = data;
    if (![x, y, viewportWidth, viewportHeight].every(Number.isFinite) || viewportWidth <= 0 || viewportHeight <= 0
      || x < 0 || y < 0 || x >= viewportWidth || y >= viewportHeight) return false;
    const rect = canvas.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) return false;
    const clientX = rect.left + x / viewportWidth * rect.width;
    const clientY = rect.top + y / viewportHeight * rect.height;
    const button = Number.isInteger(data.button) && data.button >= 0 && data.button <= 2 ? data.button : 0;
    const mask = [1, 4, 2][button];
    if (type === "down") { buttons |= mask; down = { button, clientX, clientY, dragged: false }; }
    if (type === "up") buttons &= ~mask;
    if (type === "move" && down && Math.hypot(clientX - down.clientX, clientY - down.clientY) > 5) down.dragged = true;
    const fields = { clientX, clientY, button: type === "move" ? -1 : button, buttons,
      pointerId: 1, pointerType: "mouse", isPrimary: true };
    if (type === "wheel") {
      if (![data.deltaX, data.deltaY].every(Number.isFinite)) return false;
      send(WheelEvent, "wheel", { ...fields, deltaMode: 0, deltaX: data.deltaX, deltaY: data.deltaY });
    } else {
      const suffix = { move: "move", down: "down", up: "up" }[type];
      send(PointerEvent, "pointer" + suffix, fields);
      send(MouseEvent, "mouse" + suffix, fields);
      if (type === "up") {
        if (button === 0 && down?.button === 0 && !down.dragged
          && Math.hypot(clientX - down.clientX, clientY - down.clientY) <= 5) send(MouseEvent, "click", fields);
        down = null;
      }
    }
    return true;
  };
}
