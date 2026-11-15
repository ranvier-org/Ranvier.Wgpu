// Browser tests: the compiled package draws, picks and rejects bad WGSL in a headless browser on WebGPU, and
// its results are compared with the .NET package's (golden/scene.json and golden/scene.rgba, written on this
// machine by `npm run golden`).
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

// The tolerance between the browser's frame and the .NET package's, in steps of 255 per colour channel. The
// two are drawn by different WebGPU implementations (Dawn and wgpu-native) through the same graphics API.
//  - A pixel is "off" when any channel differs by more than OFF_THRESHOLD. At most MAX_OFF_FRACTION of the
//    pixels may be off: an edge where one of the four MSAA samples falls on the other side.
//  - No channel may differ by more than MAX_CHANNEL_DIFFERENCE, which is one MSAA sample of four at full contrast.
//  - The mean difference over all channels is at most MAX_MEAN_DIFFERENCE.
const OFF_THRESHOLD = 8;
const MAX_OFF_FRACTION = 0.001;
const MAX_CHANNEL_DIFFERENCE = 64;
const MAX_MEAN_DIFFERENCE = 0.05;
// Largest difference allowed in a picked world position, in world units.
const MAX_POSITION_DIFFERENCE = 1e-3;

let server, browser, page;

before(async () => {
  const bundle = await rolldown({ input: here("./generated/BrowserChecks.js"), logLevel: "silent" });
  const { output: [chunk] } = await bundle.generate({ format: "iife", name: "WgpuChecks", exports: "named" });
  await bundle.close();
  const html = `<!doctype html><html><body style="margin:0;background:#000"><canvas id="target"></canvas><script src="/checks.js"></script></body></html>`;
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

// Runs one exported check against the page's canvas. Byte arrays come back as base64.
const run = name => page.evaluate(async name => {
  const base64 = bytes => { let text = ""; for (let i = 0; i < bytes.length; i += 0x8000) text += String.fromCharCode(...bytes.subarray(i, i + 0x8000)); return btoa(text); };
  const result = await globalThis.WgpuChecks[name](document.getElementById("target"));
  for (const key of Object.keys(result)) if (result[key] instanceof Uint8Array) result[key] = base64(result[key]);
  return result;
}, name);

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

let scene;
const sceneResult = async () => scene ??= await run("scene");

test("the fixed scene drawn in the browser matches the .NET render within the tolerance", async () => {
  const result = await sceneResult();
  const { width, height } = golden.scene;
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
  fs.writeFileSync(here("./artifacts/scene-browser.png"), png(pixels, width, height));
  fs.writeFileSync(here("./artifacts/scene-native.png"), png(nativePixels, width, height));
  fs.writeFileSync(here("./artifacts/scene-difference-x32.png"), png(amplified, width, height));
  console.log(`browser adapter "${result.adapter}" (${result.backend}); .NET adapter "${golden.adapter}" (${golden.backend})`);
  console.log(`pixels ${difference.pixels}; differing ${difference.differing}; largest channel difference ${difference.largest}/255; mean channel difference ${difference.mean.toFixed(5)}/255; pixels differing by more than ${OFF_THRESHOLD}: ${difference.over}`);
  console.log(`encode and submit: ${result.gpuRenderMs.toFixed(3)} ms (first frame)`);
  assert.ok(difference.largest <= MAX_CHANNEL_DIFFERENCE, `largest channel difference ${difference.largest} exceeds ${MAX_CHANNEL_DIFFERENCE}`);
  assert.ok(difference.mean <= MAX_MEAN_DIFFERENCE, `mean channel difference ${difference.mean} exceeds ${MAX_MEAN_DIFFERENCE}`);
  assert.ok(difference.over <= difference.pixels * MAX_OFF_FRACTION, `${difference.over} pixels differ by more than ${OFF_THRESHOLD}`);
});

test("a frame drawn after picking equals the frame drawn before it", async () => {
  const result = await sceneResult();
  assert.equal(result.again, result.pixels);
  assert.deepEqual([result.renderCalls, result.framesDelivered], [2, 2]);
});

test("PickAsync returns the pick id and world position of the .NET Pick for each chosen pixel", async () => {
  const result = await sceneResult();
  assert.equal(result.picks.length, golden.picks.length);
  let largest = 0;
  result.picks.forEach((pick, i) => {
    const expected = golden.picks[i];
    assert.deepEqual([pick.x, pick.y], [expected.x, expected.y]);
    assert.equal(pick.pickId, expected.pickId, `pick id at (${pick.x}, ${pick.y})`);
    if (expected.pickId === 0) return;
    const position = new Float32Array(new Uint32Array(expected.positionBits).buffer);
    const exact = pick.position.every((value, c) => Object.is(Math.fround(value), position[c]));
    const difference = Math.max(...pick.position.map((value, c) => Math.abs(value - position[c])));
    largest = Math.max(largest, difference);
    console.log(`pick (${pick.x}, ${pick.y}) id ${pick.pickId}: browser [${pick.position.join(", ")}] .NET [${[...position].join(", ")}] ${exact ? "bit-equal" : `differs by ${difference.toExponential(2)}`}`);
    assert.ok(difference <= MAX_POSITION_DIFFERENCE, `world position at (${pick.x}, ${pick.y}) differs by ${difference}`);
  });
  console.log(`largest world position difference ${largest.toExponential(2)}`);
});

test("two picks started together both resolve, and a pixel outside the target gives ValueNone", async () => {
  const result = await sceneResult();
  assert.deepEqual(result.together.map(pick => pick.pickId), [golden.picks[0].pickId, golden.picks[1].pickId]);
  assert.equal(result.outsideIsNone, true);
});

test("a shader built with Shaders.composeScene draws, reads the user uniform block, and survives a resize", async () => {
  const result = await run("composed");
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

test("WGSL with only vs_main and fs_main draws meshes, and lines, discs and picking use the stock shader", async () => {
  const result = await run("meshOnly");
  assert.equal(result.drawCalls, 3);
  near(result.box, [255, 0, 255, 255], 0, "the custom fs_main painted the box");
  assert.ok(result.lineRed > 90, `the stock line shader drew the hairline (red ${result.lineRed})`);
  assert.ok(result.disc[1] > 200 && result.disc[0] < 60, `the stock disc shader drew the disc (${result.disc})`);
  assert.equal(result.boxPickId, 9);
  assert.equal(result.discPickId, 5);
});

test("bad WGSL is rejected with a WgslException that carries the browser's message", async () => {
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
