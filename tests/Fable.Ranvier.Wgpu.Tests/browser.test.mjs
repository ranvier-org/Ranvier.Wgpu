// Browser tests: the compiled package draws, picks and rejects bad shaders in a headless browser, on WebGPU and
// on WebGL2, and its results are compared with the .NET package's (golden/scene.json and golden/scene.rgba,
// written on this machine by `npm run golden`).
//
// The browser is one already installed: WGPU_TEST_BROWSER names a Playwright channel (default "chrome";
// "msedge" also works). Nothing is downloaded. The frames compared are written to artifacts/ as PNG files.
import test, { after, before } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import http from "node:http";
import zlib from "node:zlib";
import { fileURLToPath } from "node:url";
import { rolldown } from "rolldown";
import { chromium } from "playwright-core";

const here = name => fileURLToPath(new URL(name, import.meta.url));
const golden = JSON.parse(fs.readFileSync(here("./golden/scene.json"), "utf8"));
const nativePixels = fs.readFileSync(here("./golden/scene.rgba"));

// The tolerance between the browser's frame and the .NET package's, in steps of 255 per colour channel. On
// WebGPU the two are drawn by different WebGPU implementations (Dawn and wgpu-native) through the same
// graphics API. On WebGL2 the browser draws through ANGLE on Direct3D 11, and the backend hands the rasteriser
// the triangles WebGPU hands it, so one tolerance holds both backends.
//  - A pixel is "off" when any channel differs by more than OFF_THRESHOLD. At most MAX_OFF_FRACTION of the
//    pixels may be off: an edge where one of the four MSAA samples falls on the other side.
//  - No channel may differ by more than MAX_CHANNEL_DIFFERENCE, which is one MSAA sample of four at full contrast.
//  - The mean difference over all channels is at most MAX_MEAN_DIFFERENCE.
const OFF_THRESHOLD = 8;
const MAX_OFF_FRACTION = 0.001;
const MAX_CHANNEL_DIFFERENCE = 64;
const MAX_MEAN_DIFFERENCE = 0.05;
// Between two WebGL2 frames of one scene: at most MAX_REPEAT_FRACTION of the pixels may differ, each by at
// most MAX_REPEAT_CHANNEL_DIFFERENCE. Measured on ANGLE Direct3D 11: 10 of 76,800 pixels, by 1.
const MAX_REPEAT_CHANNEL_DIFFERENCE = 2;
const MAX_REPEAT_FRACTION = 0.0005;
// Largest difference allowed in a picked world position, in world units.
const MAX_POSITION_DIFFERENCE = 1e-3;
const BACKENDS = ["WebGPU", "WebGL2"];

let server, browser, page;

before(async () => {
  const bundle = await rolldown({ input: here("./generated/BrowserChecks.js"), logLevel: "silent" });
  const { output: [chunk] } = await bundle.generate({ format: "iife", name: "WgpuChecks", exports: "named" });
  await bundle.close();
  // A canvas gives out one kind of context for its whole life, so each backend has its own.
  const html = `<!doctype html><html><body style="margin:0;background:#000"><canvas id="target"></canvas><canvas id="target-webgl2"></canvas><script src="/checks.js"></script></body></html>`;
  server = http.createServer((request, response) => {
    const script = request.url === "/checks.js";
    response.writeHead(200, { "content-type": script ? "text/javascript" : "text/html" });
    response.end(script ? chunk.code : html);
  });
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  browser = await chromium.launch({
    channel: process.env.WGPU_TEST_BROWSER ?? "chrome",
    headless: true,
    args: ["--enable-unsafe-webgpu", "--enable-gpu", "--ignore-gpu-blocklist"],
  });
  page = await browser.newPage();
  page.on("console", message => { if (message.type() === "error" || message.type() === "warning") console.log(`[page ${message.type()}] ${message.text()}`); });
  await page.goto(`http://127.0.0.1:${server.address().port}/`);
});

after(async () => {
  await browser?.close();
  await new Promise(resolve => server?.close(resolve));
});

// Runs one exported check against the page's canvas for a backend. Byte arrays come back as base64.
const run = (name, backend = "WebGPU") => page.evaluate(async ([name, backend]) => {
  const base64 = bytes => { let text = ""; for (let i = 0; i < bytes.length; i += 0x8000) text += String.fromCharCode(...bytes.subarray(i, i + 0x8000)); return btoa(text); };
  const result = await globalThis.WgpuChecks[name](document.getElementById(backend === "WebGL2" ? "target-webgl2" : "target"), backend);
  for (const key of Object.keys(result)) if (result[key] instanceof Uint8Array) result[key] = base64(result[key]);
  return result;
}, [name, backend]);

// Asks for a context on a canvas of its own, on a page whose navigator.gpu is as `gpu` says: "present" leaves
// it alone, "absent" hides it, and "no adapter" makes requestAdapter resolve to null.
const choose = (backend, gpu = "present", contextBefore = null) => page.evaluate(async ([backend, gpu, contextBefore]) => {
  if (gpu === "absent") Object.defineProperty(navigator, "gpu", { value: undefined, configurable: true });
  if (gpu === "no adapter") Object.defineProperty(navigator, "gpu", { value: { requestAdapter: async () => null }, configurable: true });
  try {
    const canvas = document.createElement("canvas");
    if (contextBefore) canvas.getContext(contextBefore.kind, contextBefore.attributes);
    return await globalThis.WgpuChecks.chosen(canvas, backend);
  } finally {
    delete navigator.gpu;
  }
}, [backend, gpu, contextBefore]);

const png = (rgba, width, height) => {
  const raw = Buffer.alloc((width * 4 + 1) * height);
  for (let y = 0; y < height; y++) rgba.copy(raw, y * (width * 4 + 1) + 1, y * width * 4, (y + 1) * width * 4);
  const chunk = (type, data) => {
    const body = Buffer.concat([Buffer.from(type), data]);
    const length = Buffer.alloc(4); length.writeUInt32BE(data.length);
    const crc = Buffer.alloc(4); crc.writeUInt32BE(zlib.crc32(body) >>> 0);
    return Buffer.concat([length, body, crc]);
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0); header.writeUInt32BE(height, 4); header[8] = 8; header[9] = 6;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk("IHDR", header), chunk("IDAT", zlib.deflateSync(raw)), chunk("IEND", Buffer.alloc(0))]);
};

const compare = (a, b) => {
  let largest = 0, sum = 0, differing = 0, over = 0;
  for (let i = 0; i < a.length; i += 4) {
    let pixel = 0;
    for (let c = 0; c < 4; c++) { const d = Math.abs(a[i + c] - b[i + c]); sum += d; if (d > pixel) pixel = d; }
    if (pixel > 0) differing++;
    if (pixel > OFF_THRESHOLD) over++;
    if (pixel > largest) largest = pixel;
  }
  return { largest, mean: sum / a.length, differing, over, pixels: a.length / 4 };
};

const close = (actual, expected, tolerance) => Math.abs(actual - expected) <= tolerance;
const near = (pixel, expected, tolerance, what) =>
  assert.ok(pixel.every((value, i) => close(value, expected[i], tolerance)), `${what}: got ${pixel}, expected ${expected} within ${tolerance}`);

const scenes = {};
const everyHook = {};
const sceneResult = async backend => scenes[backend] ??= await run("scene", backend);

for (const backend of BACKENDS) {
  // The WebGPU artifacts keep the names they had before there was a second backend.
  const suffix = backend === "WebGPU" ? "" : `-${backend.toLowerCase()}`;

  test(`[${backend}] the fixed scene drawn in the browser matches the .NET render within the tolerance`, async () => {
    const result = await sceneResult(backend);
    const { width, height } = golden.scene;
    assert.equal(result.backend, backend);
    assert.deepEqual([result.width, result.height], [width, height]);
    assert.deepEqual([result.drawCalls, result.instances, result.triangles], [golden.scene.drawCalls, golden.scene.instances, golden.scene.triangles]);
    const pixels = Buffer.from(result.pixels, "base64");
    assert.equal(pixels.length, nativePixels.length);
    const difference = compare(pixels, nativePixels);
    const amplified = Buffer.alloc(pixels.length);
    for (let i = 0; i < pixels.length; i += 4) {
      for (let c = 0; c < 3; c++) amplified[i + c] = Math.min(255, Math.abs(pixels[i + c] - nativePixels[i + c]) * 32);
      amplified[i + 3] = 255;
    }
    fs.mkdirSync(here("./artifacts"), { recursive: true });
    fs.writeFileSync(here(`./artifacts/scene-browser${suffix}.png`), png(pixels, width, height));
    fs.writeFileSync(here("./artifacts/scene-native.png"), png(nativePixels, width, height));
    fs.writeFileSync(here(`./artifacts/scene-difference${suffix}-x32.png`), png(amplified, width, height));
    console.log(`browser adapter "${result.adapter}" (${result.backend}); .NET adapter "${golden.adapter}" (${golden.backend})`);
    console.log(`pixels ${difference.pixels}; differing ${difference.differing}; largest channel difference ${difference.largest}/255; mean channel difference ${difference.mean.toFixed(5)}/255; pixels differing by more than ${OFF_THRESHOLD}: ${difference.over}`);
    console.log(`encode and submit: ${result.gpuRenderMs.toFixed(3)} ms (first frame)`);
    assert.ok(difference.largest <= MAX_CHANNEL_DIFFERENCE, `largest channel difference ${difference.largest} exceeds ${MAX_CHANNEL_DIFFERENCE}`);
    assert.ok(difference.mean <= MAX_MEAN_DIFFERENCE, `mean channel difference ${difference.mean} exceeds ${MAX_MEAN_DIFFERENCE}`);
    assert.ok(difference.over <= difference.pixels * MAX_OFF_FRACTION, `${difference.over} pixels differ by more than ${OFF_THRESHOLD}`);
  });

  test(`[${backend}] the canvas shows the frame that ReadPixelsAsync returns, the right way up`, async () => {
    const result = await sceneResult(backend);
    const { width, height } = golden.scene;
    // The backdrop is opaque, so the canvas holds the same bytes whether or not it is premultiplied.
    const difference = compare(Buffer.from(result.shown, "base64"), Buffer.from(result.pixels, "base64"));
    fs.writeFileSync(here(`./artifacts/scene-canvas${suffix}.png`), png(Buffer.from(result.shown, "base64"), width, height));
    console.log(`canvas against readback: differing ${difference.differing} of ${difference.pixels}; largest channel difference ${difference.largest}/255`);
    assert.equal(difference.differing, 0);
  });

  test(`[${backend}] a frame drawn after picking equals the frame drawn before it`, async () => {
    const result = await sceneResult(backend);
    if (backend === "WebGPU") {
      assert.equal(result.again, result.pixels);
    } else {
      // Two WebGL2 frames of one scene are not always equal on ANGLE's Direct3D 11 backend: where a hairline
      // lies under a blended mesh, a channel of a few pixels can move by one step, with no pick in between.
      // The same calls give equal frames on ANGLE's Vulkan backend and on SwiftShader. A pick that disturbed
      // the scene uniforms would move a whole frame, so a narrow tolerance still shows it.
      const difference = compare(Buffer.from(result.again, "base64"), Buffer.from(result.pixels, "base64"));
      console.log(`frame after picking against the frame before: differing ${difference.differing} of ${difference.pixels}; largest channel difference ${difference.largest}/255`);
      assert.ok(difference.largest <= MAX_REPEAT_CHANNEL_DIFFERENCE, `largest channel difference ${difference.largest} exceeds ${MAX_REPEAT_CHANNEL_DIFFERENCE}`);
      assert.ok(difference.differing <= difference.pixels * MAX_REPEAT_FRACTION, `${difference.differing} pixels differ`);
    }
    assert.deepEqual([result.renderCalls, result.framesDelivered], [2, 2]);
  });

  test(`[${backend}] PickAsync returns the pick id and world position of the .NET Pick for each chosen pixel`, async () => {
    const result = await sceneResult(backend);
    assert.equal(result.picks.length, golden.picks.length);
    let largest = 0, exactCount = 0, hits = 0;
    result.picks.forEach((pick, i) => {
      const expected = golden.picks[i];
      assert.deepEqual([pick.x, pick.y], [expected.x, expected.y]);
      assert.equal(pick.pickId, expected.pickId, `pick id at (${pick.x}, ${pick.y})`);
      if (expected.pickId === 0) return;
      const position = new Float32Array(new Uint32Array(expected.positionBits).buffer);
      const exact = pick.position.every((value, c) => Object.is(Math.fround(value), position[c]));
      const difference = Math.max(...pick.position.map((value, c) => Math.abs(value - position[c])));
      largest = Math.max(largest, difference);
      hits++;
      if (exact) exactCount++;
      console.log(`pick (${pick.x}, ${pick.y}) id ${pick.pickId}: browser [${pick.position.join(", ")}] .NET [${[...position].join(", ")}] ${exact ? "bit-equal" : `differs by ${difference.toExponential(2)}`}`);
      assert.ok(difference <= MAX_POSITION_DIFFERENCE, `world position at (${pick.x}, ${pick.y}) differs by ${difference}`);
    });
    console.log(`${exactCount} of ${hits} hit positions bit-equal; largest world position difference ${largest.toExponential(2)}`);
  });

  test(`[${backend}] two picks started together both resolve, and a pixel outside the target gives ValueNone`, async () => {
    const result = await sceneResult(backend);
    assert.deepEqual(result.together.map(pick => pick.pickId), [golden.picks[0].pickId, golden.picks[1].pickId]);
    assert.equal(result.outsideIsNone, true);
  });

  test(`[${backend}] a shader built with composeScene draws, reads the user uniform block, and survives a resize`, async () => {
    const result = await run("composed", backend);
    assert.equal(result.backend, backend);
    assert.equal(result.drawCalls, 1);
    // lit = (1, 0, 0) through the stock tone mapping: pow(clamp(1 / 1.86 * 1.36), 1 / 2.15) = 0.8645.
    near(result.centre, [220, 0, 0, 255], 2, "centre of the box under the composed shader");
    assert.deepEqual([...result.centre], golden.customCentre, "the .NET package draws the same centre pixel");
    near(result.corner, [0, 0, 0, 255], 0, "backdrop");
    assert.equal(result.pickId, 7);
    near(result.recolouredCentre, [0, 0, 220, 255], 2, "centre after one user uniform slot is rewritten");
    // Resize to 40x56: the context, the canvas and the readback all take the new size, and drawing and picking go on.
    assert.deepEqual([...result.resizedSize], [40, 56, 40, 56, 40 * 56 * 4]);
    near(result.resizedCentre, [0, 0, 220, 255], 2, "centre after a resize");
    near(result.resizedCorner, [0, 0, 0, 255], 0, "backdrop after a resize");
    assert.equal(result.resizedPickId, 7);
  });

  test(`[${backend}] a shader with only vs_main and fs_main draws meshes, and lines, discs and picking use the stock shader`, async () => {
    const result = await run("meshOnly", backend);
    assert.equal(result.backend, backend);
    assert.equal(result.drawCalls, 3);
    near(result.box, [255, 0, 255, 255], 0, "the custom fs_main painted the box");
    assert.ok(result.lineRed > 90, `the stock line shader drew the hairline (red ${result.lineRed})`);
    assert.ok(result.disc[1] > 200 && result.disc[0] < 60, `the stock disc shader drew the disc (${result.disc})`);
    assert.equal(result.boxPickId, 9);
    assert.equal(result.discPickId, 5);
  });

  test(`[${backend}] geometry nearer than the near plane is clipped: clip-space depth runs from 0 to w`, async () => {
    const result = await run("nearPlane", backend);
    assert.equal(result.backend, backend);
    // The green box behind shows, unlit: pow(clamp(1 / 1.86 * 1.36), 1 / 2.15) = 0.8645.
    near(result.centre, [0, 220, 0, 255], 2, "centre, where the red box in front of the near plane would be");
    assert.equal(result.pickId, 2);
    // The front face of the green box, a unit box at the origin seen along -z.
    assert.ok(close(result.position[2], 0.5, 1e-4), `picked z ${result.position[2]}`);
  });

  test(`[${backend}] code at each of the six hook points is compiled where it belongs and changes the frame`, async () => {
    const result = everyHook[backend] = await run("everyHook", backend);
    assert.equal(result.backend, backend);
    console.log(`lit box ${result.lit}; unlit box ${result.unlit}`);
    // The lit box: tint(vec3(1, 1, height)) with the tint (1, 0.5, 0.25) and a height near 0.5, tone mapped.
    // 1 gives 220 and 0.5 gives 185; the blue is whatever the height at that pixel gives.
    near(result.lit.slice(0, 2), [220, 185], 2, "red and green of the lit box");
    assert.ok(result.lit[2] > 90 && result.lit[2] < 130, `blue of the lit box, from the varying: ${result.lit[2]}`);
    // The unlit box returns early from FragmentBegin with its height in the blue channel, not tone mapped.
    near(result.unlit.slice(0, 2), [0, 0], 0, "red and green of the unlit box");
    assert.ok(result.unlit[2] > 110 && result.unlit[2] < 140, `blue of the unlit box: ${result.unlit[2]}`);
    assert.deepEqual([result.litPickId, result.unlitPickId], [1, 2]);
    assert.deepEqual([result.validWgsl, result.validGlsl], [true, true]);
  });

  test(`[${backend}] a write of some user uniform slots lands on those slots and leaves the others`, async () => {
    const result = await run("userSlots", backend);
    assert.equal(result.backend, backend);
    near(result.firstTwo, [0, 220, 0, 255], 2, "slot 1 green, slot 15 untouched");
    near(result.lastSlot, [220, 220, 0, 255], 2, "slot 15 red added");
    near(result.firstTwoAgain, [220, 0, 220, 255], 2, "slot 1 blue, slot 15 still red");
  });

  test(`[${backend}] a custom shader given only in the other API's language is refused, not replaced by the stock shader`, async () => {
    const result = await run("otherLanguage", backend);
    console.log(`refusal: ${result.only.message}`);
    assert.equal(result.only.rejected, true);
    if (backend === "WebGL2") {
      assert.equal(result.only.isGlslException, true);
      assert.match(result.only.message, /WebGL2/);
      assert.match(result.only.message, /custom WGSL and no custom GLSL/);
      assert.match(result.only.message, /customGlsl/);
      // Broken WGSL is refused although WebGL2 would never compile it.
      assert.equal(result.brokenOther.isWgslException, true);
    } else {
      assert.equal(result.only.isWgslException, true);
      assert.match(result.only.message, /WebGPU/);
      assert.match(result.only.message, /custom GLSL and no custom WGSL/);
      assert.match(result.only.message, /customWgsl/);
      assert.equal(result.brokenOther.isGlslException, true);
    }
    assert.match(result.brokenOther.message, /Missing: fs_main/);
    assert.equal(result.backendAfter, backend);
  });

  test(`[${backend}] a pick still waiting when the context is disposed settles`, async () => {
    const result = await run("pickThenDispose", backend);
    console.log(`pick after Dispose: ${result.settled}${result.message ? `: ${result.message}` : ""}`);
    assert.ok(result.settled === "rejected" || result.pickId === 7, JSON.stringify(result));
  });

  test(`[${backend}] Camera3D.orthographic preserves pixel size across depth and supports DiscBatch, projectToScreen, screenRay, and PickAsync`, async () => {
    const result = await run("orthographicScene", backend);
    assert.equal(result.backend, backend);
    assert.ok(result.nearWidth > 20, `near sphere renders across multiple pixels (${result.nearWidth})`);
    assert.ok(Math.abs(result.nearWidth - result.farWidth) <= 1, `orthographic spheres differ in width: ${result.nearWidth} vs ${result.farWidth}`);
    assert.ok(result.discPixel[1] > 200 && result.discPixel[0] < 60, `orthographic disc pixel should be green (${result.discPixel})`);
    assert.equal(result.nearPick.pickId, 101);
    assert.equal(result.farPick.pickId, 102);
    assert.equal(result.discPick.pickId, 103);
    assert.ok(Math.abs(result.nearPick.position[0] - (-0.8)) < 0.02, `near picked X ${result.nearPick.position[0]}`);
    assert.ok(Math.abs(result.farPick.position[0] - 0.8) < 0.02, `far picked X ${result.farPick.position[0]}`);
    assert.ok(close(result.screenNear[0], 48.0, 1e-3) && close(result.screenNear[1], 80.0, 1e-3));
    assert.ok(close(result.screenFar[0], 112.0, 1e-3) && close(result.screenFar[1], 80.0, 1e-3));
    assert.ok(close(result.rayOrigAX, -0.8, 1e-4) && close(result.rayOrigBX, 0.8, 1e-4));
    for (const dir of [result.rayDirA, result.rayDirB]) {
      assert.ok(close(dir[0], 0, 1e-4) && close(dir[1], 0, 1e-4) && close(dir[2], -1, 1e-4));
    }
  });

  test(`[${backend}] MeshHandle.UpdateVertices, UpdateVertexRange, and UpdateMesh update rendered pixels and rebind grown buffers`, async () => {
    const result = await run("dynamicMesh", backend);
    assert.equal(result.backend, backend);
    assert.deepEqual([...result.initialCounts], [4, 6, 2]);
    assert.ok(result.px0[0] > 200 && result.px0[1] < 40 && result.px0[2] < 40, `initial quad red: ${result.px0}`);
    assert.equal(result.pick0.pickId, 7);
    assert.ok(close(result.pick0.position[2], 0.0, 1e-3), `initial Z ${result.pick0.position[2]}`);
    assert.ok(result.px1[0] < 40 && result.px1[1] > 200 && result.px1[2] < 40, `updated quad green: ${result.px1}`);
    assert.equal(result.pick1.pickId, 7);
    assert.ok(close(result.pick1.position[2], 0.5, 1e-3), `updated Z ${result.pick1.position[2]}`);
    assert.ok(result.topLeft[2] > 200 && result.topLeft[2] > result.topLeft[1], `top-left blue after UpdateVertexRange: ${result.topLeft}`);
    assert.ok(result.botRight[1] > 200 && result.botRight[2] < 40, `bottom-right green after UpdateVertexRange: ${result.botRight}`);
    assert.equal(result.negThrows, true);
    assert.equal(result.oobThrows, true);
    assert.deepEqual([...result.vertGrownCounts], [8, 6, 2]);
    assert.ok(result.vertGrownPixel[0] > 200 && result.vertGrownPixel[1] > 200 && result.vertGrownPixel[2] < 40, `vertex-only grown quad yellow: ${result.vertGrownPixel}`);
    assert.equal(result.vertGrownPick.pickId, 7);
    assert.ok(close(result.vertGrownPick.position[2], 0.75, 1e-3), `vertex-only grown Z ${result.vertGrownPick.position[2]}`);
    assert.deepEqual([...result.idxGrownCounts], [4, 12, 4]);
    assert.equal(result.idxGrownTris, 4);
    assert.ok(result.idxGrownPixel[0] > 200 && result.idxGrownPixel[1] < 40, `index-grown quad red: ${result.idxGrownPixel}`);
    assert.equal(result.idxGrownPick.pickId, 7);
    assert.deepEqual([...result.boxCounts], [24, 36, 12]);
    assert.equal(result.boxTris, 12);
    assert.ok(result.boxPixel[0] > 200 && result.boxPixel[1] > 200 && result.boxPixel[2] > 200, `grown box white: ${result.boxPixel}`);
    assert.equal(result.boxPick.pickId, 7);
    assert.equal(result.nullMeshThrows, true);
    assert.equal(result.disposedVertsThrows, true);
    assert.equal(result.disposedRangeThrows, true);
    assert.equal(result.disposedMeshThrows, true);
  });

  test(`[${backend}] InstanceBatch, LineBatch, and DiscBatch Resize and UpdateRange mutate only target slots and validate ranges`, async () => {
    const result = await run("dynamicBatches", backend);
    assert.equal(result.backend, backend);

    // InstanceBatch
    assert.ok(result.inst.pxLeft0[0] > 200 && result.inst.pxLeft0[1] < 40, `slot 0 red: ${result.inst.pxLeft0}`);
    assert.ok(result.inst.pxMid0[0] > 200 && result.inst.pxMid0[1] > 200 && result.inst.pxMid0[2] < 40, `slot 1 yellow: ${result.inst.pxMid0}`);
    assert.ok(result.inst.pxRight0[2] > 200 && result.inst.pxRight0[0] < 40, `slot 2 blue: ${result.inst.pxRight0}`);
    assert.deepEqual([...result.inst.picks0], [10, 99, 12]);
    assert.equal(result.inst.count2, 2);
    assert.deepEqual([...result.inst.pxRight2], [0, 0, 0, 255]);
    assert.deepEqual([...result.inst.picks2], [10, 99, 0]);
    assert.equal(result.inst.count5, 5);
    assert.deepEqual([...result.inst.picks5], [10, 99, 0]);
    assert.deepEqual([...result.inst.picks20], [10, 99, 199]);
    assert.deepEqual([...result.inst.singlePicks], [77, 78]);
    assert.equal(result.inst.negResize, true);
    assert.equal(result.inst.negRange, true);
    assert.equal(result.inst.oobRange, true);
    assert.equal(result.inst.disposedThrows, true);

    // LineBatch
    assert.ok(result.line.topRed0 > 120, `line 0 red: ${result.line.topRed0}`);
    assert.ok(result.line.midYellow0 > 120, `line 1 yellow: ${result.line.midYellow0}`);
    assert.ok(result.line.botBlue0 > 120, `line 2 blue: ${result.line.botBlue0}`);
    assert.deepEqual([...result.line.counts4], [4, 2]);
    assert.equal(result.line.botBlue4, 0);
    assert.equal(result.line.botBlue6, 0);
    assert.ok(result.line.topRed70 > 120, `line 0 preserved on capacity growth: ${result.line.topRed70}`);
    assert.ok(result.line.botCyan70 > 120, `line 34 cyan after capacity growth: ${result.line.botCyan70}`);
    assert.equal(result.line.negResize, true);
    assert.equal(result.line.oddResize, true);
    assert.equal(result.line.oddStart, true);
    assert.equal(result.line.oddLen, true);
    assert.equal(result.line.negStart, true);
    assert.equal(result.line.oobRange, true);
    assert.equal(result.line.disposedThrows, true);

    // DiscBatch
    assert.deepEqual([...result.disc.picks0], [20, 88, 22]);
    assert.equal(result.disc.count2, 2);
    assert.equal(result.disc.right2, 0);
    assert.equal(result.disc.right5, 0);
    assert.deepEqual([...result.disc.picks40], [20, 299]);
    assert.equal(result.disc.negResize, true);
    assert.equal(result.disc.oobRange, true);
    assert.equal(result.disc.disposedThrows, true);
  });

  test(`[${backend}] LineVertex3D(pos, col, true) renders inside clipped SectionPlane half-space while false is clipped`, async () => {
    const result = await run("lineSectionPlane", backend);
    assert.equal(result.backend, backend);
    assert.ok(result.unclippedGreen > 120, `unclipped line green: ${result.unclippedGreen}`);
    assert.equal(result.clippedRed, 0, `clipped line with ignoreSectionPlane=false must discard: ${result.clippedRed}`);
    assert.ok(result.exemptRgbaBlue > 120, `exempt RgbaColor line blue: ${result.exemptRgbaBlue}`);
    assert.ok(result.exemptVec4Cyan > 120, `exempt Vector4 line cyan: ${result.exemptVec4Cyan}`);
  });

  test(`[${backend}] alpha-zero mesh and disc occluders do not block PickAsync of opaque targets behind them`, async () => {
    const result = await run("alphaZeroPick", backend);
    assert.equal(result.backend, backend);
    assert.equal(result.meshPickId, 42, `expected opaque mesh behind alpha=0 mesh: got ${result.meshPickId}`);
    assert.equal(result.discPickId, 42, `expected opaque disc behind alpha=0 disc: got ${result.discPickId}`);
  });

  test(`[${backend}] InstanceFlags.ExactColor emits exact [0, 255, 0, 255] while InstanceFlags.Unlit keeps [0, 220, 0, 255]`, async () => {
    const result = await run("exactColorCheck", backend);
    assert.equal(result.backend, backend);
    assert.deepEqual([...result.flags], [0, 1, 2, 4, 8, 16]);
    near(result.exactGreen, [0, 255, 0, 255], 1, "ExactColor pure green");
    near(result.exactMid, [51, 102, 204, 255], 1, "ExactColor mid-tone");
    near(result.unlitGreen, [0, 220, 0, 255], 2, "Unlit tone-mapped green");
  });

  test(`[${backend}] DrawBeforeLines renders backdrop AlphaBlend panel before LineBatch hairlines`, async () => {
    const result = await run("drawBeforeLinesCheck", backend);
    assert.equal(result.backend, backend);
    assert.deepEqual([...result.flags], [false, true, false]);
    assert.ok(result.redWithBefore > 100, `white line after backdrop stays bright (${result.redWithBefore})`);
    assert.ok(result.redWithAfter < 80, `translucent panel after line dims white line (${result.redWithAfter})`);
    assert.ok(result.redWithBefore > result.redWithAfter, `drawBeforeLines=true (${result.redWithBefore}) > false (${result.redWithAfter})`);
  });

  test(`[${backend}] CreateSingleBatch renders and picks immediately without manual UpdateInstances`, async () => {
    const result = await run("createSingleBatchCheck", backend);
    assert.equal(result.backend, backend);
    assert.equal(result.defaultCount, 1);
    assert.equal(result.customCount, 1);
    near(result.centre, [0, 255, 0, 255], 1, "CreateSingleBatch green centre");
    assert.equal(result.pickId, 77);
  });

  test(`[${backend}] RenderPasses, PickPassesAsync, and ViewportRect isolate viewports, clear vs preserve color, and scale discs`, async () => {
    const result = await run("viewportRenderPassesCheck", backend);
    assert.equal(result.backend, backend);
    assert.equal(result.splitDrawCalls, 2);
    assert.equal(result.splitInstances, 2);
    assert.equal(result.renderCallsDelta, 2);
    assert.equal(result.framesDelta, 1);
    near(result.leftBg, [102, 0, 0, 255], 2, "left clear");
    near(result.leftObj, [0, 255, 0, 255], 1, "left green box");
    near(result.rightBg, [0, 0, 102, 255], 2, "right clear");
    near(result.rightObj, [255, 255, 0, 255], 1, "right yellow box");
    assert.equal(result.leftOutPickId, 0);
    assert.equal(result.rightOutPickId, 0);
    assert.equal(result.pickLeftId, 101);
    assert.equal(result.pickRightId, 202);
    assert.equal(result.pickOutsideId, 0);
    // Disc in 160x100 sub-viewport keeps symmetric 10px radius
    assert.ok(result.discRight7[1] > 200, `disc covers +7px X (${result.discRight7})`);
    assert.ok(result.discDown7[1] > 200, `disc covers +7px Y (${result.discDown7})`);
    assert.ok(result.discDown15[1] < 20, `disc excludes +15px Y (${result.discDown15})`);
    // Overlapping ClearColor = ValueSome clears only overlayVp while keeping outer backdrop
    near(result.overlapOuter, [102, 0, 0, 255], 2, "outer backdrop preserved");
    near(result.overlapClearCorner, [0, 0, 102, 255], 2, "overlayVp cleared to rightClear");
    near(result.overlapClearCenter, [255, 255, 0, 255], 1, "overlayVp box drawn on top");
    assert.equal(result.overlapClearPickCenterId, 202);
    assert.equal(result.overlapClearPickCornerId, 0);
    // Overlapping ClearColor = ValueNone preserves pass 0 color in overlayVp while clearing depth
    near(result.overlapPreserveCorner, [0, 255, 0, 255], 1, "overlayVp ClearColor=None preserves pass 0 green box color");
    near(result.overlapPreserveCenter, [0, 255, 255, 255], 1, "overlayVp ClearColor=None clears depth so deeper cyan box draws");
    assert.equal(result.overlapPreservePickId, 303);
    // Bounds & empty passes & guards
    assert.equal(result.clippedDraws, 2);
    assert.equal(result.offscreenDraws, 0);
    assert.equal(result.emptyDraws, 0);
    near(result.emptyPixel, [0, 102, 0, 255], 2, "empty RenderPasses clears to explicit clearColor");
    assert.equal(result.nullPassesThrows, true);
    assert.equal(result.disposedThrows, true);
  });
}

test("the WebGL2 frame and picks of the fixed scene equal the WebGPU ones", async () => {
  const [gpu, gl] = [await sceneResult("WebGPU"), await sceneResult("WebGL2")];
  const difference = compare(Buffer.from(gl.pixels, "base64"), Buffer.from(gpu.pixels, "base64"));
  console.log(`WebGL2 against WebGPU: differing ${difference.differing} of ${difference.pixels}; largest channel difference ${difference.largest}/255`);
  assert.ok(difference.largest <= MAX_CHANNEL_DIFFERENCE, `largest channel difference ${difference.largest} exceeds ${MAX_CHANNEL_DIFFERENCE}`);
  assert.ok(difference.over <= difference.pixels * MAX_OFF_FRACTION, `${difference.over} pixels differ by more than ${OFF_THRESHOLD}`);
  assert.deepEqual(gl.picks.map(pick => pick.pickId), gpu.picks.map(pick => pick.pickId));
  const unequal = gl.picks.filter((pick, i) => !pick.position.every((value, c) => Object.is(value, gpu.picks[i].position[c])));
  console.log(`picked positions that differ between the backends: ${unequal.length} of ${gl.picks.length}`);
  assert.deepEqual([gl.drawCalls, gl.instances, gl.triangles], [gpu.drawCalls, gpu.instances, gpu.triangles]);
});

test("the GLSL hooks draw what the WGSL hooks draw", () => {
  const [gpu, gl] = [everyHook.WebGPU, everyHook.WebGL2];
  near(gl.lit, gpu.lit, 1, "the box coloured through Declarations, Varyings, VertexBegin, VertexEnd and FragmentLit");
  near(gl.unlit, gpu.unlit, 1, "the box that returns from FragmentBegin");
});

test("[WebGPU] bad WGSL is rejected with a WgslException that carries the browser's message", async () => {
  const result = await run("badWgsl");
  console.log(`compile failure: ${result.compile.message.replace(/\s+/g, " ").slice(0, 300)}`);
  console.log(`pipeline failure: ${result.pipeline.message.replace(/\s+/g, " ").slice(0, 300)}`);
  console.log(`missing entry point: ${result.missing.message}`);
  for (const failure of [result.compile, result.pipeline, result.missing]) {
    assert.equal(failure.rejected, true);
    assert.equal(failure.isWgslException, true);
  }
  assert.match(result.compile.message, /^WGSL failed to compile\. /);
  assert.match(result.compile.message, /notDeclaredAnywhere/);
  assert.match(result.pipeline.message, /^WGSL does not fit the scene pipelines\. /);
  assert.match(result.missing.message, /fs_main/);
  assert.deepEqual([result.validCompile.ok, result.validPipeline.ok, result.validMissing.ok], [false, false, false]);
  assert.equal(result.validCompile.message, result.compile.message);
  assert.equal(result.validPipeline.message, result.pipeline.message);
  assert.equal(result.validMissing.message, result.missing.message);
  assert.deepEqual([result.validStock.ok, result.validComposed.ok], [true, true]);
});

test("[WebGL2] bad GLSL is rejected with a GlslException that carries the driver's log", async () => {
  const result = await run("badGlsl", "WebGL2");
  console.log(`compile failure: ${result.compile.message.replace(/\s+/g, " ").slice(0, 300)}`);
  console.log(`pipeline failure: ${result.pipeline.message.replace(/\s+/g, " ").slice(0, 300)}`);
  console.log(`link failure: ${result.link.message.replace(/\s+/g, " ").slice(0, 300)}`);
  console.log(`loose uniform: ${result.loose.message.replace(/\s+/g, " ").slice(0, 300)}`);
  console.log(`missing entry point: ${result.missing.message}`);
  for (const failure of [result.compile, result.pipeline, result.link, result.loose, result.missing]) {
    assert.equal(failure.rejected, true);
    assert.equal(failure.isGlslException, true);
  }
  assert.match(result.compile.message, /^GLSL failed to compile\. vs_main: /);
  assert.match(result.compile.message, /notDeclaredAnywhere/);
  // The driver's line number is a line of the text that was given, not of what the backend put before it.
  assert.match(result.compile.message, new RegExp(`\\b0:${result.unresolvedLine}:`));
  assert.match(result.pipeline.message, /^GLSL does not fit the scene pipelines\. vs_main with fs_main: /);
  assert.match(result.pipeline.message, /localPosBits at location 0 has the base type uint/);
  assert.match(result.link.message, /^GLSL does not fit the scene pipelines\. vs_main with fs_main: /);
  assert.match(result.link.message, /v_neverWritten/);
  assert.match(result.loose.message, /brightness is outside a uniform block/);
  assert.match(result.missing.message, /^Custom GLSL must define the entry points vs_main and fs_main\. Missing: fs_main\.$/);
  assert.deepEqual([result.validCompile.ok, result.validPipeline.ok, result.validLink.ok, result.validMissing.ok], [false, false, false, false]);
  assert.equal(result.validCompile.message, result.compile.message);
  assert.equal(result.validPipeline.message, result.pipeline.message);
  assert.equal(result.validLink.message, result.link.message);
  assert.equal(result.validMissing.message, result.missing.message);
  assert.deepEqual([result.validStock.ok, result.validComposed.ok, result.validMeshOnly.ok], [true, true, true]);
  assert.equal(result.backendAfter, "WebGL2");
});

test("[WebGL2] the driver lays the uniform blocks out at the offsets Packing.fs writes", async () => {
  const layout = await page.evaluate(() => globalThis.WgpuChecks.uniformLayout());
  const blocks = Object.fromEntries(layout.blocks.map(block => [block.name, block.bytes]));
  // 176 bytes: a mat4 and seven vec4. 256 bytes: sixteen vec4.
  assert.deepEqual(blocks, { SceneUniforms: 176, UserUniforms: 256 });
  const offsets = Object.fromEntries(layout.members.map(member => [member.name.replace(/\[0\]$/, ""), member.offset]));
  // Packing.sceneUniforms writes float 0, 16, 20, 24, 28, 32, 36 and 40: four bytes each.
  assert.deepEqual(offsets, {
    "SceneUniforms.viewProj": 0,
    "SceneUniforms.camPos": 64,
    "SceneUniforms.clipPlane": 80,
    "SceneUniforms.skyColor": 96,
    "SceneUniforms.groundColor": 112,
    "SceneUniforms.keyLight": 128,
    "SceneUniforms.rimLight": 144,
    "SceneUniforms.sectionStyle": 160,
    "UserUniforms.slots": 0,
  });
  const slots = layout.members.find(member => member.name.startsWith("UserUniforms.slots"));
  assert.equal(slots.arrayStride, 16, "a user uniform slot is 16 bytes");
});

test("the backend asked for is the backend used, and Auto falls back to WebGL2 where there is no WebGPU adapter", async () => {
  const report = async (...asked) => { const result = await choose(...asked); console.log(`${asked.join(", ")}: ${result.backend || result.message}${result.adapter ? ` on "${result.adapter}"` : ""}`); return result; };
  assert.equal((await report("WebGPU")).backend, "WebGPU");
  assert.equal((await report("WebGL2")).backend, "WebGL2");
  assert.equal((await report("Auto")).backend, "WebGPU");
  assert.equal((await report("Auto", "absent")).backend, "WebGL2");
  assert.equal((await report("Auto", "no adapter")).backend, "WebGL2");
  assert.match((await report("WebGPU", "absent")).message, /navigator\.gpu is not available/);
  assert.match((await report("WebGPU", "no adapter")).message, /returned no adapter/);
});

test("[WebGL2] a canvas that cannot take the frame is refused with the reason", async () => {
  const other = await choose("WebGL2", "present", { kind: "2d" });
  console.log(`canvas with a 2d context: ${other.message}`);
  assert.match(other.message, /^WgpuContext failed to initialize WebGL2: the canvas gave no webgl2 context\./);
  const multisampled = await choose("WebGL2", "present", { kind: "webgl2", attributes: { antialias: true } });
  console.log(`canvas whose webgl2 context is antialiased: ${multisampled.backend || multisampled.message}`);
  assert.match(multisampled.message, /^WgpuContext failed to initialize WebGL2: the canvas's drawing buffer does not take the frame/);
});
