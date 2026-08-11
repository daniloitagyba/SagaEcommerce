import os
import sys

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, SCRIPT_DIR)
from icons import ICONS

W, H = 2000, 1384

# Dracula palette
BG = "#191a21"
BOX_BG = "#282a36"
FG = "#f8f8f2"
COMMENT = "#6272a4"
CYAN = "#8be9fd"
GREEN = "#50fa7b"
ORANGE = "#ffb86c"
PINK = "#ff79c6"
PURPLE = "#bd93f9"
YELLOW = "#f1fa8c"


def brand_icon(name, size=26, color=FG):
    d = ICONS[name]
    return f'<svg width="{size}" height="{size}" viewBox="0 0 24 24" fill="{color}" style="flex:none;vertical-align:middle;margin-right:10px"><path d="{d}"/></svg>'


def glyph_icon(svg_inner, size=26, color=FG):
    return f'<svg width="{size}" height="{size}" viewBox="0 0 24 24" fill="none" stroke="{color}" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" style="flex:none;vertical-align:middle;margin-right:10px">{svg_inner}</svg>'


# Simple monoline glyphs for this project's OWN services (no official
# third-party logo applies to them - only infrastructure/tool boxes below
# get real brand marks).
GLYPHS = {
    "client": '<rect x="2" y="4" width="16" height="11" rx="1"/><path d="M8 20h4M10 15v5"/><circle cx="19" cy="17" r="3"/><path d="M19 15.5v1.5l1 1"/>',
    "bag": '<path d="M6 7h12l1 13H5L6 7z"/><path d="M9 7a3 3 0 0 1 6 0"/>',
    "cart": '<circle cx="9" cy="20" r="1"/><circle cx="18" cy="20" r="1"/><path d="M2 3h2l2.4 12.4a2 2 0 0 0 2 1.6h8.6a2 2 0 0 0 2-1.6L21 7H6"/>',
    "list": '<path d="M4 6h16M4 12h16M4 18h10"/>',
    "doc": '<path d="M6 2h9l5 5v15H6z"/><path d="M15 2v5h5"/>',
    "gear": '<circle cx="12" cy="12" r="3"/><path d="M12 2v3M12 19v3M4.2 4.2l2.1 2.1M17.7 17.7l2.1 2.1M2 12h3M19 12h3M4.2 19.8l2.1-2.1M17.7 6.3l2.1-2.1"/>',
    "card": '<rect x="2" y="5" width="20" height="14" rx="2"/><path d="M2 10h20"/>',
    "cube": '<path d="M12 2 3 7v10l9 5 9-5V7z"/><path d="M3 7l9 5 9-5M12 12v10"/>',
    "orbit": '<circle cx="12" cy="12" r="3"/><ellipse cx="12" cy="12" rx="10" ry="4"/>',
    "branch": '<circle cx="6" cy="4" r="2"/><circle cx="6" cy="20" r="2"/><circle cx="18" cy="10" r="2"/><path d="M6 6v10M6 12c0-4 4-2 12-2"/>',
}


def box(x, y, w, h, border, title_icon, title, subtitle=None, title_color=FG):
    sub = f'<div style="color:{COMMENT};font-size:15px;margin-top:6px;line-height:1.4">{subtitle}</div>' if subtitle else ""
    return f'''
    <div style="position:absolute;left:{x}px;top:{y}px;width:{w}px;height:{h}px;
                background:{BOX_BG};border:2px solid {border};border-radius:10px;
                box-sizing:border-box;padding:14px 18px;display:flex;flex-direction:column;
                justify-content:center;font-family:'JetBrains Mono',Menlo,Consolas,monospace">
      <div style="display:flex;align-items:center;font-size:19px;font-weight:700;color:{title_color}">
        {title_icon}{title}
      </div>
      {sub}
    </div>'''


def panel(x, y, w, h, border, title_icon, title, title_color, items):
    lis = "".join(
        f'<div style="display:flex;align-items:flex-start;margin:14px 0;font-size:16px;color:{FG}">'
        f'{ic}<span>{txt}</span></div>'
        for ic, txt in items
    )
    return f'''
    <div style="position:absolute;left:{x}px;top:{y}px;width:{w}px;height:{h}px;
                background:{BOX_BG};border:2px solid {border};border-radius:10px;
                box-sizing:border-box;padding:22px 28px;font-family:'JetBrains Mono',Menlo,Consolas,monospace">
      <div style="display:flex;align-items:center;font-size:21px;font-weight:700;color:{title_color};margin-bottom:6px">
        {title_icon}{title}
      </div>
      {lis}
    </div>'''


def arrow(x1, y1, x2, y2, color, marker, sw=2.2):
    return f'<line x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}" stroke="{color}" stroke-width="{sw}" marker-end="url(#{marker})"/>'


boxes = []
arrows_svg = []

# ---- Title ----
boxes.append(f'<div style="position:absolute;left:0;top:32px;width:{W}px;text-align:center;'
             f'font-family:Menlo,Consolas,monospace;font-size:44px;font-weight:800;color:{FG};letter-spacing:1px">SagaEcommerce</div>')

# ---- Client ----
client_x, client_y, client_w, client_h = 800, 134, 400, 62
boxes.append(box(client_x, client_y, client_w, client_h, COMMENT,
                  glyph_icon(GLYPHS["client"], color=FG),
                  '<span style="white-space:nowrap">Client (Web / Mobile)</span>'))

# ---- Linkerd / Keycloak ----
lk_y, lk_h = 232, 78
linkerd_x, linkerd_w = 630, 380
keycloak_x, keycloak_w = 1090, 390
boxes.append(box(linkerd_x, lk_y, linkerd_w, lk_h, CYAN, brand_icon("linkerd", color=CYAN), "Linkerd", "mTLS service mesh &mdash; ingress", CYAN))
boxes.append(box(keycloak_x, lk_y, keycloak_w, lk_h, YELLOW, brand_icon("keycloak", color=YELLOW), "Keycloak", "OIDC &mdash; JWT authn/authz", YELLOW))

# arrows client -> linkerd/keycloak: aimed at each box's top-center rather
# than a fixed inset from its edge, so they land cleanly under the title
# instead of hugging the box's outer corner.
arrows_svg.append(arrow(client_x + 60, client_y + client_h, linkerd_x + linkerd_w / 2, lk_y, COMMENT, "arrow-grey"))
arrows_svg.append(arrow(client_x + client_w - 60, client_y + client_h, keycloak_x + keycloak_w / 2, lk_y, COMMENT, "arrow-grey"))

# ---- Storefront ----
sf_x, sf_y, sf_w, sf_h = 760, 359, 480, 88
boxes.append(box(sf_x, sf_y, sf_w, sf_h, PURPLE, glyph_icon(GLYPHS["bag"], color=PURPLE), "Storefront.Service", "BFF &mdash; checkout, cart &amp; product-summary fan-out", PURPLE))

arrows_svg.append(arrow(linkerd_x + linkerd_w - 90, lk_y + lk_h, sf_x + sf_w * 0.28, sf_y, COMMENT, "arrow-grey"))
arrows_svg.append(arrow(keycloak_x + 90, lk_y + lk_h, sf_x + sf_w * 0.72, sf_y, COMMENT, "arrow-grey"))

# ---- 6 service boxes ----
svc_y, svc_h = 496, 96
svc_gap = 18
svc_w = (W - 120 - svc_gap * 5) // 6
svc_x0 = 60
services = [
    ("cart", "Cart.Service", "Redis system-of-record"),
    ("list", "Catalog.Service", "MongoDB documents"),
    ("doc", "Orders.Api", "Line items &mdash; outbox to Kafka"),
    ("gear", "Orders.Worker", "Lifecycle &mdash; orchestrated saga"),
    ("card", "Payments.Service", "Scored risk &mdash; authorize then capture"),
    ("cube", "Inventory.Service", "Per-SKU partitions &mdash; warehouses + backorders"),
]
svc_positions = []
for i, (icon_key, name, sub) in enumerate(services):
    x = svc_x0 + i * (svc_w + svc_gap)
    svc_positions.append((x, svc_y, svc_w, svc_h))
    boxes.append(box(x, svc_y, svc_w, svc_h, PURPLE, glyph_icon(GLYPHS[icon_key], size=22, color=PURPLE), name, sub, PURPLE))
    arrows_svg.append(arrow(sf_x + 70 + i * 66, sf_y + sf_h, x + svc_w / 2, svc_y, COMMENT, "arrow-grey"))

# ---- Kafka ----
kafka_x, kafka_y, kafka_w, kafka_h = 720, 630, 1220, 74
boxes.append(box(kafka_x, kafka_y, kafka_w, kafka_h, PINK, brand_icon("kafka", size=26, color=PINK), "Kafka &mdash; event backbone",
                  "Avro + Schema Registry &mdash; Debezium CDC &mdash; topics: orders, payments, inventory, saga-timeouts", PINK))

# Straight vertical drops: Kafka's box is wide enough to span every
# source's own x-center, so the target needn't be squeezed into a
# narrower range - that's what was producing the sharp leftward slant.
kafka_feed_xs = []
for i, (x, y, w, h) in enumerate(svc_positions[2:], start=2):
    cx = x + w / 2
    kafka_feed_xs.append(cx)
    arrows_svg.append(arrow(cx, y + h, cx, kafka_y, PINK, "arrow-pink"))

# ---- Redis / MongoDB / PostgreSQL ----
store_y, store_h = 764, 76
redis_x, redis_w = 60, 300
mongo_x, mongo_w = 380, 300
pg_x, pg_w = 700, 1240
boxes.append(box(redis_x, store_y, redis_w, store_h, GREEN, brand_icon("redis", size=24, color=GREEN), "Redis", "cart + cache", GREEN))
boxes.append(box(mongo_x, store_y, mongo_w, store_h, GREEN, brand_icon("mongodb", size=24, color=GREEN), "MongoDB", "catalog", GREEN))
boxes.append(box(pg_x, store_y, pg_w, store_h, GREEN, brand_icon("postgresql", size=24, color=GREEN), "PostgreSQL", "orders &mdash; payments &mdash; inventory &mdash; outbox/inbox", GREEN))

arrows_svg.append(arrow(svc_positions[0][0] + svc_w / 2, svc_y + svc_h, redis_x + redis_w / 2, store_y, GREEN, "arrow-green"))
arrows_svg.append(arrow(svc_positions[1][0] + svc_w / 2, svc_y + svc_h, mongo_x + mongo_w / 2, store_y, GREEN, "arrow-green"))
for cx in kafka_feed_xs:
    arrows_svg.append(arrow(cx, kafka_y + kafka_h, cx, store_y, GREEN, "arrow-green"))

# ---- Observability / GitOps panels ----
panel_y, panel_h = 890, 300
obs_x, obs_w = 60, 940
gitops_x, gitops_w = 1030, 910

obs_items = [
    (brand_icon("opentelemetry", size=20, color=CYAN), "OpenTelemetry &mdash; traces, metrics, logs"),
    (brand_icon("prometheus", size=20, color=CYAN), "Prometheus &mdash; metrics + burn-rate alerts"),
    (brand_icon("grafana", size=20, color=CYAN), "Grafana &mdash; dashboards"),
    (glyph_icon(GLYPHS["orbit"], size=20, color=CYAN), "Tempo &mdash; Loki &mdash; traces &amp; logs"),
]
gitops_items = [
    (brand_icon("argo", size=20, color=ORANGE), "Argo CD &mdash; reconciles K3s from main"),
    (brand_icon("argo", size=20, color=ORANGE), "Argo Rollouts &mdash; canary + analysis template"),
    (glyph_icon(GLYPHS["gear"], size=20, color=ORANGE), "Kyverno &mdash; signed-image admission policy"),
    (glyph_icon(GLYPHS["orbit"], size=20, color=ORANGE), "HPA + KEDA &mdash; CPU &amp; Kafka-lag autoscaling"),
]
boxes.append(panel(obs_x, panel_y, obs_w, panel_h, CYAN, glyph_icon(GLYPHS["orbit"], color=CYAN), "Observability", CYAN, obs_items))
boxes.append(panel(gitops_x, panel_y, gitops_w, panel_h, ORANGE, glyph_icon(GLYPHS["branch"], color=ORANGE), "GitOps &amp; Platform", ORANGE, gitops_items))

arrows_svg.append(arrow(redis_x + redis_w / 2, store_y + store_h, obs_x + 260, panel_y, CYAN, "arrow-cyan"))
arrows_svg.append(arrow(pg_x + pg_w / 2, store_y + store_h, gitops_x + gitops_w / 2, panel_y, ORANGE, "arrow-orange"))

# ---- Footer ----
foot_y = panel_y + panel_h + 46
boxes.append(f'<div style="position:absolute;left:0;top:{foot_y}px;width:{W}px;text-align:center;'
             f'font-family:Menlo,Consolas,monospace;font-size:17px;color:{FG}">7 .NET services &mdash; event-driven sagas &mdash; polyglot persistence &mdash; full GitOps deployment</div>')
boxes.append(f'<div style="position:absolute;left:0;top:{foot_y + 30}px;width:{W}px;text-align:center;'
             f'font-family:Menlo,Consolas,monospace;font-size:16px;color:{COMMENT}">github.com/daniloitagyba/SagaEcommerce</div>')

# ---- Legend ----
legend_y = foot_y + 74
legend_items = [
    (PURPLE, "services"), (PINK, "messaging"), (GREEN, "data stores"),
    (CYAN, "observability"), (ORANGE, "GitOps/platform"), (YELLOW, "security"),
]
legend_html = ""
lx = 0
widths = []
for color, label in legend_items:
    widths.append(24 + 10 + len(label) * 10 + 48)
total_w = sum(widths)
lx = (W - total_w) / 2
for (color, label), w in zip(legend_items, widths):
    legend_html += (f'<div style="position:absolute;left:{lx}px;top:{legend_y}px;display:flex;align-items:center;'
                     f'font-family:Menlo,Consolas,monospace;font-size:16px;color:{FG}">'
                     f'<span style="width:20px;height:20px;background:{color};border-radius:4px;margin-right:10px;display:inline-block"></span>{label}</div>')
    lx += w
boxes.append(legend_html)

svg_defs = f'''
<svg width="{W}" height="{H}" style="position:absolute;left:0;top:0;pointer-events:none">
  <defs>
    <marker id="arrow-grey" markerWidth="8" markerHeight="8" refX="6" refY="4" orient="auto"><path d="M0,0 L8,4 L0,8 Z" fill="{COMMENT}"/></marker>
    <marker id="arrow-pink" markerWidth="8" markerHeight="8" refX="6" refY="4" orient="auto"><path d="M0,0 L8,4 L0,8 Z" fill="{PINK}"/></marker>
    <marker id="arrow-green" markerWidth="8" markerHeight="8" refX="6" refY="4" orient="auto"><path d="M0,0 L8,4 L0,8 Z" fill="{GREEN}"/></marker>
    <marker id="arrow-cyan" markerWidth="8" markerHeight="8" refX="6" refY="4" orient="auto"><path d="M0,0 L8,4 L0,8 Z" fill="{CYAN}"/></marker>
    <marker id="arrow-orange" markerWidth="8" markerHeight="8" refX="6" refY="4" orient="auto"><path d="M0,0 L8,4 L0,8 Z" fill="{ORANGE}"/></marker>
  </defs>
  {''.join(arrows_svg)}
</svg>
'''

html = f'''<!doctype html>
<html><head><meta charset="utf-8">
<style>
  html,body {{ margin:0; padding:0; background:{BG}; }}
  #canvas {{ position:relative; width:{W}px; height:{H}px; overflow:hidden; }}
</style>
</head>
<body>
<div id="canvas">
  {svg_defs}
  {''.join(boxes)}
</div>
</body></html>
'''

with open(os.path.join(SCRIPT_DIR, "diagram.html"), "w") as f:
    f.write(html)

print("written", len(html), "bytes")
