// Raid Announcer upload service (Cloudflare Worker + Workers KV).
//
// POST /upload          a zip of fight logs from the program ("Upload logs"); header X-Upload-Token must match
//                       UPLOAD_TOKEN (shipped with the program, not a secret – it only keeps random scanners out).
//                       Limits: 20 MB, must be a zip, 30 uploads per day and network. No IP address is stored.
// GET  /admin/list      list of uploads (Authorization: Bearer ADMIN_TOKEN, a secret only the raid lead has)
// GET  /admin/file/<k>  download one upload
// DELETE /admin/file/<k> delete one upload
//
// Storage: KV namespace UPLOADS, key "u/<UTC time>_<random>.zip", metadata { size, version, fights, client }.

const MAX_BYTES = 20 * 1024 * 1024;
const PER_DAY = 30;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    try {
      if (url.pathname === "/upload" && request.method === "POST") return await upload(request, env);
      if (url.pathname.startsWith("/admin/")) return await admin(request, env, url);
      if (url.pathname === "/") return text("Raid Announcer upload service");
      return text("not found", 404);
    } catch (e) {
      return text("error: " + (e && e.message ? e.message : e), 500);
    }
  },
};

async function upload(request, env) {
  if (request.headers.get("x-upload-token") !== env.UPLOAD_TOKEN) return text("forbidden", 403);
  const declared = Number(request.headers.get("content-length") || "0");
  if (declared > MAX_BYTES) return text("too large (max 20 MB)", 413);
  const body = await request.arrayBuffer();
  if (body.byteLength === 0) return text("empty", 400);
  if (body.byteLength > MAX_BYTES) return text("too large (max 20 MB)", 413);
  const head = new Uint8Array(body, 0, Math.min(4, body.byteLength));
  if (!(head[0] === 0x50 && head[1] === 0x4b && head[2] === 0x03 && head[3] === 0x04)) return text("not a zip", 415);

  // daily limit per network, counted under a salted hash so no address is ever stored
  const day = new Date().toISOString().slice(0, 10);
  const ip = request.headers.get("cf-connecting-ip") || "unknown";
  const quotaKey = "q/" + day + "/" + (await sha256(day + "|" + ip + "|" + env.UPLOAD_TOKEN)).slice(0, 24);
  const used = Number((await env.UPLOADS.get(quotaKey)) || "0");
  if (used >= PER_DAY) return text("daily limit reached, try again tomorrow", 429);
  await env.UPLOADS.put(quotaKey, String(used + 1), { expirationTtl: 2 * 24 * 3600 });

  const stamp = new Date().toISOString().replace(/[:.]/g, "-");
  const key = "u/" + stamp + "_" + crypto.randomUUID().slice(0, 8) + ".zip";
  const meta = {
    size: body.byteLength,
    version: clip(request.headers.get("x-app-version")),
    fights: Number(request.headers.get("x-fights") || "0") || 0,
    client: clip(request.headers.get("x-client-id")),
  };
  await env.UPLOADS.put(key, body, { metadata: meta });
  return json({ ok: true, key, size: body.byteLength });
}

async function admin(request, env, url) {
  if (!env.ADMIN_TOKEN || request.headers.get("authorization") !== "Bearer " + env.ADMIN_TOKEN) return text("forbidden", 403);
  if (url.pathname === "/admin/list" && request.method === "GET") {
    const items = [];
    let cursor;
    do {
      const page = await env.UPLOADS.list({ prefix: "u/", cursor });
      for (const k of page.keys) items.push({ key: k.name, ...(k.metadata || {}) });
      cursor = page.list_complete ? undefined : page.cursor;
    } while (cursor);
    return json({ uploads: items });
  }
  if (url.pathname.startsWith("/admin/file/")) {
    const key = decodeURIComponent(url.pathname.slice("/admin/file/".length));
    if (!key.startsWith("u/")) return text("bad key", 400);
    if (request.method === "GET") {
      const value = await env.UPLOADS.get(key, { type: "arrayBuffer" });
      if (value === null) return text("not found", 404);
      return new Response(value, { headers: { "content-type": "application/zip" } });
    }
    if (request.method === "DELETE") {
      await env.UPLOADS.delete(key);
      return json({ ok: true });
    }
  }
  return text("not found", 404);
}

function clip(v) {
  return v ? String(v).slice(0, 64) : null;
}

async function sha256(s) {
  const d = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s));
  return [...new Uint8Array(d)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

function text(body, status = 200) {
  return new Response(body, { status, headers: { "content-type": "text/plain; charset=utf-8" } });
}

function json(obj, status = 200) {
  return new Response(JSON.stringify(obj), { status, headers: { "content-type": "application/json" } });
}
