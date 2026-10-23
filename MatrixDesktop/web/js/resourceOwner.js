// Bind native methods to their real receiver; only allocated textures and buffers belong
// to the pass. Borrowed inputs, views, samplers and pipeline objects are never destroyed.
export const ownDevice = (owner, device) => new Proxy(device, {
	get: (target, key) => {
		const value = target[key];
		if (key === "createBuffer" || key === "createTexture") return (...args) => {
			if (owner.closed) throw new DOMException("Renderer closed", "AbortError");
			return owner.own(value.apply(target, args));
		};
		return typeof value === "function" ? value.bind(target) : value;
	},
});

export const ownRegl = (owner, regl) => new Proxy(regl, {
	get: (target, key) => {
		const value = target[key];
		if (["texture", "framebuffer", "renderbuffer", "buffer", "elements"].includes(key)) {
			return (...args) => owner.own(value.apply(target, args));
		}
		return typeof value === "function" ? value.bind(target) : value;
	},
});

// Tracks only resources allocated by one pass. Inputs from preceding passes are borrowed.
// Late asynchronous loads are destroyed immediately when their owner has already closed.
export default () => {
	const resources = new Set();
	let closed = false;
	const release = (resource) => {
		if (resource != null && resources.delete(resource)) resource.destroy();
	};
	return {
		get closed() { return closed; },
		own: (resource) => {
			if (resource != null) {
				if (closed) resource.destroy();
				else resources.add(resource);
			}
			return resource;
		},
		release,
		cleanup: () => {
			closed = true;
			for (const resource of resources) release(resource);
		},
	};
};
