"""Temp helper: call Unity MCP tools via raw HTTP (bridge port moved to 8083).
Usage: python unity_mcp.py <tool_name> [json_args]
"""
import json, sys, urllib.request

URL = "http://127.0.0.1:8083/mcp"

def post(payload, sid=None):
    req = urllib.request.Request(URL, data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json",
                 "Accept": "application/json, text/event-stream"})
    if sid: req.add_header("mcp-session-id", sid)
    with urllib.request.urlopen(req, timeout=180) as r:
        body = r.read().decode()
        out_sid = r.headers.get("mcp-session-id")
    # SSE streams may contain ": ping" comments before data lines — always extract data:
    data_lines = [l[5:].strip() for l in body.splitlines() if l.startswith("data:")]
    data = "\n".join(data_lines) if data_lines else body
    return out_sid, data

def call_tool(name, args):
    sid, resp = post({"jsonrpc": "2.0", "id": 1, "method": "initialize",
        "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                   "clientInfo": {"name": "helper", "version": "1.0"}}})
    post({"jsonrpc": "2.0", "method": "notifications/initialized"}, sid)
    _, resp = post({"jsonrpc": "2.0", "id": 2, "method": "tools/call",
                    "params": {"name": name, "arguments": args}}, sid)
    return json.loads(resp)

if __name__ == "__main__":
    tool = sys.argv[1]
    args = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}
    try:
        out = call_tool(tool, args)
    except Exception as e:
        print("CALL_ERROR:", e)
        sys.exit(1)
    sc = out.get("result", {}).get("structuredContent")
    if sc is not None:
        print(json.dumps(sc, ensure_ascii=False)[:4000])
    else:
        txt = out.get("result", {}).get("content", [])
        for c in txt:
            print(str(c.get("text", ""))[:4000])
        if not txt:
            print(json.dumps(out, ensure_ascii=False)[:2000])
