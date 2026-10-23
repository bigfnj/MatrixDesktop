export function selectImages(images, source = "all") {
  return (Array.isArray(images) ? images : []).filter(image => image && typeof image.url === "string" &&
    (source === "all" || (image.group ?? "custom") === source || (Array.isArray(image.groups) && image.groups.includes(source))));
}

export function createMedia(images = []) {
  let disposed = false;
  const cache = new Map();
  const pending = new Set();
  const failures = new Set();
  const load = async (index) => {
    if (disposed || index < 0 || index >= images.length || failures.has(index)) return null;
    if (cache.has(index)) return cache.get(index);
    const entry = images[index];
    const task = (async () => {
      const image = new Image(); image.crossOrigin = "anonymous";
      pending.add(image);
      try {
        image.src = entry.url; await image.decode();
        if (disposed) return null;
        const scale = Math.min(1, 1024 / Math.max(image.naturalWidth, image.naturalHeight));
        const canvas = document.createElement("canvas");
        canvas.width = Math.max(1, Math.round(image.naturalWidth * scale));
        canvas.height = Math.max(1, Math.round(image.naturalHeight * scale));
        const context = canvas.getContext("2d", { willReadFrequently: true });
        context.drawImage(image, 0, 0, canvas.width, canvas.height);
        const pixels = context.getImageData(0, 0, canvas.width, canvas.height);
        return { ...entry, width: canvas.width, height: canvas.height, data: pixels.data };
      } catch (error) {
        if (!disposed) { failures.add(index); console.warn(`UC3D picture ${index + 1} could not be loaded: ${error.message}`); }
        return null;
      } finally { pending.delete(image); image.src = ""; }
    })();
    cache.set(index, task);
    // Only retain current and next decodes. No ever-growing slideshow cache.
    for (const key of cache.keys()) if (key !== index && key !== (index + 1) % images.length) cache.delete(key);
    return task;
  };
  return {
    images, load,
    get failedCount() { return failures.size; },
    dispose() { disposed = true; for (const image of pending) image.src = ""; pending.clear(); cache.clear(); },
  };
}
