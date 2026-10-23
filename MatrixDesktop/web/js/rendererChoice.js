// Looking Glass requires the REGL multiview rain and lenticular quilt passes.
export const wantsWebGPU = (config) => !config.useHoloplay && config.renderer?.toLowerCase() === "webgpu";
