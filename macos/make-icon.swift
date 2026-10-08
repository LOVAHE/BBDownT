// 生成 1024×1024 的应用图标 PNG：圆角底板 + 屏幕外框 + 向下箭头。用法：swift make-icon.swift out.png
import AppKit

let size: CGFloat = 1024
let out = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "icon.png"
let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size), pixelsHigh: Int(size), bitsPerSample: 8,
                           samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                           bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)

// 底板（macOS 图标网格：四周留 100px）
let plate = NSBezierPath(roundedRect: NSRect(x: 100, y: 100, width: 824, height: 824), xRadius: 185, yRadius: 185)
NSGradient(starting: NSColor(srgbRed: 0.18, green: 0.62, blue: 0.95, alpha: 1),
           ending: NSColor(srgbRed: 0.09, green: 0.33, blue: 0.78, alpha: 1))!.draw(in: plate, angle: -90)

NSColor.white.setStroke()
NSColor.white.setFill()

// 屏幕外框
let screen = NSBezierPath(roundedRect: NSRect(x: 232, y: 300, width: 560, height: 400), xRadius: 64, yRadius: 64)
screen.lineWidth = 44
screen.stroke()
// 两只"天线"
for (x1, x2) in [(400.0, 340.0), (624.0, 684.0)] {
    let a = NSBezierPath()
    a.move(to: NSPoint(x: x1, y: 700)); a.line(to: NSPoint(x: x2, y: 790))
    a.lineWidth = 40; a.lineCapStyle = .round; a.stroke()
}
// 向下箭头
let shaft = NSBezierPath(roundedRect: NSRect(x: 488, y: 430, width: 48, height: 200), xRadius: 24, yRadius: 24)
shaft.fill()
let head = NSBezierPath()
head.move(to: NSPoint(x: 400, y: 480)); head.line(to: NSPoint(x: 512, y: 360)); head.line(to: NSPoint(x: 624, y: 480))
head.lineWidth = 48; head.lineCapStyle = .round; head.lineJoinStyle = .round; head.stroke()
// 底座
let base = NSBezierPath(roundedRect: NSRect(x: 392, y: 212, width: 240, height: 40), xRadius: 20, yRadius: 20)
base.fill()

NSGraphicsContext.restoreGraphicsState()
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: out))
