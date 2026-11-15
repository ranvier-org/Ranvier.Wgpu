// Serves this folder on localhost, where a page may use WebGPU. Usage: node serve.mjs [port]
import fs from "node:fs";
import http from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.dirname(fileURLToPath(import.meta.url));
const types = { ".html": "text/html", ".js": "text/javascript" };

const server = http.createServer((request, response) => {
  const name = request.url === "/" ? "index.html" : decodeURIComponent(request.url.split("?")[0]).replace(/^\/+/, "");
  const file = path.join(root, name);
  if (!file.startsWith(root) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
    response.writeHead(404).end("Not found");
    return;
  }
  response.writeHead(200, { "content-type": types[path.extname(file)] ?? "application/octet-stream" });
  fs.createReadStream(file).pipe(response);
});

server.listen(Number(process.argv[2] ?? 0), "127.0.0.1", () => console.log(`http://127.0.0.1:${server.address().port}/`));
