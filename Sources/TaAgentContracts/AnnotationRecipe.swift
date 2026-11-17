import Foundation

public struct AnnotationRecipe: Codable, Equatable, Sendable {
    public let version: Int
    public let operations: [AnnotationOperation]

    public init(version: Int, operations: [AnnotationOperation]) {
        self.version = version
        self.operations = operations
    }

    public func validated(
        existingElementIDs: Set<String> = []
    ) throws -> ValidatedAnnotationRecipe {
        guard version == 1 else {
            throw AnnotationRecipeValidationError("不支持标注配方版本：\(version)。")
        }
        guard !operations.isEmpty else {
            throw AnnotationRecipeValidationError("标注配方至少需要一个 operation。")
        }
        guard operations.count <= AnnotationRecipeLimits.maximumOperations else {
            throw AnnotationRecipeValidationError(
                "标注配方最多包含 \(AnnotationRecipeLimits.maximumOperations) 个 operation。"
            )
        }

        var elementIDs = existingElementIDs
        var sawCrop = false
        for (index, operation) in operations.enumerated() {
            try operation.validate()
            if case .crop = operation {
                guard index == 0, !sawCrop else {
                    throw AnnotationRecipeValidationError("crop 最多出现一次且必须是第一个 operation。")
                }
                sawCrop = true
                continue
            }
            if case .eraser(let eraser) = operation {
                for targetID in eraser.targetIDs {
                    guard elementIDs.remove(targetID) != nil else {
                        throw AnnotationRecipeValidationError("eraser 找不到标注 ID：\(targetID)。")
                    }
                }
                continue
            }
            guard let id = operation.elementID else { continue }
            guard elementIDs.insert(id).inserted else {
                throw AnnotationRecipeValidationError("标注 ID 重复：\(id)。")
            }
        }
        return ValidatedAnnotationRecipe(
            version: version,
            operations: operations,
            resultingElementIDs: elementIDs
        )
    }
}

public struct ValidatedAnnotationRecipe: Equatable, Sendable {
    public let version: Int
    public let operations: [AnnotationOperation]
    public let resultingElementIDs: Set<String>
}

public struct AnnotationRecipeValidationError: Error, Equatable, LocalizedError, Sendable {
    public let message: String

    public init(_ message: String) {
        self.message = message
    }

    public var errorDescription: String? { message }
}

/// Upper bounds enforced by recipe validation. Values beyond these are rejected
/// before rendering so that renderer integer conversions can never trap.
public enum AnnotationRecipeLimits {
    public static let maximumOperations = 500
    public static let maximumCoordinate: Double = 1_000_000
    public static let maximumLineWidth: Double = 2_000
    public static let maximumFontSize: Double = 2_000
    public static let maximumDiameter: Double = 4_000
    public static let maximumMosaicScale: Double = 512
    public static let maximumBlurRadius: Double = 256
    public static let maximumMagnifyFactor: Double = 64
    public static let maximumPathPoints = 20_000
    public static let maximumTextLength = 4_000
}

public struct AnnotationPoint: Codable, Equatable, Sendable {
    public let x: Double
    public let y: Double

    public init(x: Double, y: Double) {
        self.x = x
        self.y = y
    }

    fileprivate var isFinite: Bool {
        x.isFinite && y.isFinite
            && abs(x) <= AnnotationRecipeLimits.maximumCoordinate
            && abs(y) <= AnnotationRecipeLimits.maximumCoordinate
    }
}

public struct AnnotationRect: Codable, Equatable, Sendable {
    public let x: Double
    public let y: Double
    public let width: Double
    public let height: Double

    public init(x: Double, y: Double, width: Double, height: Double) {
        self.x = x
        self.y = y
        self.width = width
        self.height = height
    }

    fileprivate var isValid: Bool {
        let limit = AnnotationRecipeLimits.maximumCoordinate
        return x.isFinite && y.isFinite && width.isFinite && height.isFinite && width > 0 && height > 0
            && abs(x) <= limit && abs(y) <= limit && width <= limit && height <= limit
    }
}

public struct AnnotationColor: Equatable, Sendable {
    public let red: Double
    public let green: Double
    public let blue: Double
    public let alpha: Double

    public init(red: Double, green: Double, blue: Double, alpha: Double = 1) {
        self.red = red
        self.green = green
        self.blue = blue
        self.alpha = alpha
    }

    public init(hex: String) throws {
        guard hex.hasPrefix("#"), hex.count == 7 || hex.count == 9 else {
            throw AnnotationRecipeValidationError("颜色必须使用 #RRGGBB 或 #RRGGBBAA：\(hex)。")
        }
        let digits = String(hex.dropFirst())
        guard let value = UInt64(digits, radix: 16) else {
            throw AnnotationRecipeValidationError("颜色包含无效十六进制字符：\(hex)。")
        }
        if digits.count == 6 {
            red = Double((value >> 16) & 0xFF) / 255
            green = Double((value >> 8) & 0xFF) / 255
            blue = Double(value & 0xFF) / 255
            alpha = 1
        } else {
            red = Double((value >> 24) & 0xFF) / 255
            green = Double((value >> 16) & 0xFF) / 255
            blue = Double((value >> 8) & 0xFF) / 255
            alpha = Double(value & 0xFF) / 255
        }
    }

    public static let red = AnnotationColor(red: 1, green: 59.0 / 255, blue: 48.0 / 255)
    public static let highlighter = AnnotationColor(red: 1, green: 214.0 / 255, blue: 10.0 / 255, alpha: 0.35)
}

extension AnnotationColor: Codable {
    public init(from decoder: Decoder) throws {
        let value = try decoder.singleValueContainer().decode(String.self)
        try self.init(hex: value)
    }

    public func encode(to encoder: Encoder) throws {
        func byte(_ value: Double) -> Int { Int((min(1, max(0, value)) * 255).rounded()) }
        let includeAlpha = alpha < 0.999
        let string = includeAlpha
            ? String(format: "#%02X%02X%02X%02X", byte(red), byte(green), byte(blue), byte(alpha))
            : String(format: "#%02X%02X%02X", byte(red), byte(green), byte(blue))
        var container = encoder.singleValueContainer()
        try container.encode(string)
    }
}

public struct AnnotationCropOperation: Codable, Equatable, Sendable {
    public let rect: AnnotationRect
    public init(rect: AnnotationRect) { self.rect = rect }
}

public struct AnnotationRectOperation: Codable, Equatable, Sendable {
    public let id: String
    public let rect: AnnotationRect
    public let color: AnnotationColor
    public let lineWidth: Double
    public let dashed: Bool

    public init(
        id: String,
        rect: AnnotationRect,
        color: AnnotationColor = .red,
        lineWidth: Double = 5,
        dashed: Bool = false
    ) {
        self.id = id
        self.rect = rect
        self.color = color
        self.lineWidth = lineWidth
        self.dashed = dashed
    }

    private enum CodingKeys: String, CodingKey { case id, rect, color, lineWidth, dashed }
    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        self.init(
            id: try values.decode(String.self, forKey: .id),
            rect: try values.decode(AnnotationRect.self, forKey: .rect),
            color: try values.decodeIfPresent(AnnotationColor.self, forKey: .color) ?? .red,
            lineWidth: try values.decodeIfPresent(Double.self, forKey: .lineWidth) ?? 5,
            dashed: try values.decodeIfPresent(Bool.self, forKey: .dashed) ?? false
        )
    }
}

public struct AnnotationArrowOperation: Codable, Equatable, Sendable {
    public let id: String
    public let start: AnnotationPoint
    public let end: AnnotationPoint
    public let color: AnnotationColor
    public let lineWidth: Double
    public let dashed: Bool

    public init(
        id: String,
        start: AnnotationPoint,
        end: AnnotationPoint,
        color: AnnotationColor = .red,
        lineWidth: Double = 5,
        dashed: Bool = false
    ) {
        self.id = id
        self.start = start
        self.end = end
        self.color = color
        self.lineWidth = lineWidth
        self.dashed = dashed
    }

    private enum CodingKeys: String, CodingKey { case id, start, end, color, lineWidth, dashed }
    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        self.init(
            id: try values.decode(String.self, forKey: .id),
            start: try values.decode(AnnotationPoint.self, forKey: .start),
            end: try values.decode(AnnotationPoint.self, forKey: .end),
            color: try values.decodeIfPresent(AnnotationColor.self, forKey: .color) ?? .red,
            lineWidth: try values.decodeIfPresent(Double.self, forKey: .lineWidth) ?? 5,
            dashed: try values.decodeIfPresent(Bool.self, forKey: .dashed) ?? false
        )
    }
}

public struct AnnotationStrokeOperation: Codable, Equatable, Sendable {
    public let id: String
    public let points: [AnnotationPoint]
    public let color: AnnotationColor
    public let lineWidth: Double
    public let dashed: Bool

    public init(
        id: String,
        points: [AnnotationPoint],
        color: AnnotationColor = .red,
        lineWidth: Double = 5,
        dashed: Bool = false
    ) {
        self.id = id
        self.points = points
        self.color = color
        self.lineWidth = lineWidth
        self.dashed = dashed
    }

    private enum CodingKeys: String, CodingKey { case id, points, color, lineWidth, dashed }
    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        self.init(
            id: try values.decode(String.self, forKey: .id),
            points: try values.decode([AnnotationPoint].self, forKey: .points),
            color: try values.decodeIfPresent(AnnotationColor.self, forKey: .color) ?? .red,
            lineWidth: try values.decodeIfPresent(Double.self, forKey: .lineWidth) ?? 5,
            dashed: try values.decodeIfPresent(Bool.self, forKey: .dashed) ?? false
        )
    }
}

public struct AnnotationTextOperation: Codable, Equatable, Sendable {
    public let id: String
    public let origin: AnnotationPoint
    public let text: String
    public let color: AnnotationColor
    public let fontSize: Double

    public init(
        id: String,
        origin: AnnotationPoint,
        text: String,
        color: AnnotationColor = .red,
        fontSize: Double = 24
    ) {
        self.id = id
        self.origin = origin
        self.text = text
        self.color = color
        self.fontSize = fontSize
    }

    private enum CodingKeys: String, CodingKey { case id, origin, text, color, fontSize }
    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        self.init(
            id: try values.decode(String.self, forKey: .id),
            origin: try values.decode(AnnotationPoint.self, forKey: .origin),
            text: try values.decode(String.self, forKey: .text),
            color: try values.decodeIfPresent(AnnotationColor.self, forKey: .color) ?? .red,
            fontSize: try values.decodeIfPresent(Double.self, forKey: .fontSize) ?? 24
        )
    }
}

public struct AnnotationNumberOperation: Codable, Equatable, Sendable {
    public let id: String
    public let center: AnnotationPoint
    public let number: Int
    public let color: AnnotationColor
    public let diameter: Double

    public init(
        id: String,
        center: AnnotationPoint,
        number: Int,
        color: AnnotationColor = .red,
        diameter: Double = 28
    ) {
        self.id = id
        self.center = center
        self.number = number
        self.color = color
        self.diameter = diameter
    }

    private enum CodingKeys: String, CodingKey { case id, center, number, color, diameter }
    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        self.init(
            id: try values.decode(String.self, forKey: .id),
            center: try values.decode(AnnotationPoint.self, forKey: .center),
            number: try values.decode(Int.self, forKey: .number),
            color: try values.decodeIfPresent(AnnotationColor.self, forKey: .color) ?? .red,
            diameter: try values.decodeIfPresent(Double.self, forKey: .diameter) ?? 28
        )
    }
}

public enum AnnotationMosaicMode: String, Codable, Equatable, Sendable {
    case rect
    case brush
}

public struct AnnotationMosaicOperation: Codable, Equatable, Sendable {
    public let id: String
    public let mode: AnnotationMosaicMode
    public let rect: AnnotationRect?
    public let points: [AnnotationPoint]?
    public let lineWidth: Double
    public let scale: Double

    public init(
        id: String,
        mode: AnnotationMosaicMode,
        rect: AnnotationRect? = nil,
        points: [AnnotationPoint]? = nil,
        lineWidth: Double = 24,
        scale: Double = 14
    ) {
        self.id = id
        self.mode = mode
        self.rect = rect
        self.points = points
        self.lineWidth = lineWidth
        self.scale = scale
    }

    private enum CodingKeys: String, CodingKey { case id, mode, rect, points, lineWidth, scale }
    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        self.init(
            id: try values.decode(String.self, forKey: .id),
            mode: try values.decode(AnnotationMosaicMode.self, forKey: .mode),
            rect: try values.decodeIfPresent(AnnotationRect.self, forKey: .rect),
            points: try values.decodeIfPresent([AnnotationPoint].self, forKey: .points),
            lineWidth: try values.decodeIfPresent(Double.self, forKey: .lineWidth) ?? 24,
            scale: try values.decodeIfPresent(Double.self, forKey: .scale) ?? 14
        )
    }
}

public struct AnnotationBlurOperation: Codable, Equatable, Sendable {
    public let id: String
    public let rect: AnnotationRect
    public let radius: Double

    public init(id: String, rect: AnnotationRect, radius: Double = 12) {
        self.id = id
        self.rect = rect
        self.radius = radius
    }

    private enum CodingKeys: String, CodingKey { case id, rect, radius }
    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        self.init(
            id: try values.decode(String.self, forKey: .id),
            rect: try values.decode(AnnotationRect.self, forKey: .rect),
            radius: try values.decodeIfPresent(Double.self, forKey: .radius) ?? 12
        )
    }
}

public struct AnnotationMagnifyOperation: Codable, Equatable, Sendable {
    public let id: String
    public let rect: AnnotationRect
    public let factor: Double

    public init(id: String, rect: AnnotationRect, factor: Double = 2) {
        self.id = id
        self.rect = rect
        self.factor = factor
    }

    private enum CodingKeys: String, CodingKey { case id, rect, factor }
    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        self.init(
            id: try values.decode(String.self, forKey: .id),
            rect: try values.decode(AnnotationRect.self, forKey: .rect),
            factor: try values.decodeIfPresent(Double.self, forKey: .factor) ?? 2
        )
    }
}

public struct AnnotationEraserOperation: Codable, Equatable, Sendable {
    public let targetIDs: [String]
    public init(targetIDs: [String]) { self.targetIDs = targetIDs }

    private enum CodingKeys: String, CodingKey { case targetIDs = "targetIds" }
}

public enum AnnotationOperation: Equatable, Sendable {
    case crop(AnnotationCropOperation)
    case rectangle(AnnotationRectOperation)
    case ellipse(AnnotationRectOperation)
    case arrow(AnnotationArrowOperation)
    case pen(AnnotationStrokeOperation)
    case highlighter(AnnotationStrokeOperation)
    case text(AnnotationTextOperation)
    case number(AnnotationNumberOperation)
    case mosaic(AnnotationMosaicOperation)
    case blur(AnnotationBlurOperation)
    case magnify(AnnotationMagnifyOperation)
    case eraser(AnnotationEraserOperation)

    public var elementID: String? {
        switch self {
        case .crop, .eraser: nil
        case .rectangle(let value), .ellipse(let value): value.id
        case .arrow(let value): value.id
        case .pen(let value), .highlighter(let value): value.id
        case .text(let value): value.id
        case .number(let value): value.id
        case .mosaic(let value): value.id
        case .blur(let value): value.id
        case .magnify(let value): value.id
        }
    }

    fileprivate func validate() throws {
        func requireID(_ id: String) throws {
            guard !id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                throw AnnotationRecipeValidationError("标注 id 不能为空。")
            }
        }
        func requireRect(_ rect: AnnotationRect, name: String) throws {
            guard rect.isValid else {
                throw AnnotationRecipeValidationError(
                    "\(name) 需要有限且大于 0 的宽高，坐标与尺寸不能超过 \(Int(AnnotationRecipeLimits.maximumCoordinate))。"
                )
            }
        }
        func requireWidth(_ width: Double, name: String, maximum: Double) throws {
            guard width.isFinite, width > 0, width <= maximum else {
                throw AnnotationRecipeValidationError(
                    "\(name) 必须是大于 0 且不超过 \(Int(maximum)) 的有限数字。"
                )
            }
        }
        func requirePointCount(_ count: Int, name: String) throws {
            guard count <= AnnotationRecipeLimits.maximumPathPoints else {
                throw AnnotationRecipeValidationError(
                    "\(name) 最多包含 \(AnnotationRecipeLimits.maximumPathPoints) 个坐标。"
                )
            }
        }
        let maxLine = AnnotationRecipeLimits.maximumLineWidth
        switch self {
        case .crop(let value):
            try requireRect(value.rect, name: "crop.rect")
        case .rectangle(let value), .ellipse(let value):
            try requireID(value.id)
            try requireRect(value.rect, name: "rect")
            try requireWidth(value.lineWidth, name: "lineWidth", maximum: maxLine)
        case .arrow(let value):
            try requireID(value.id)
            guard value.start.isFinite, value.end.isFinite, value.start != value.end else {
                throw AnnotationRecipeValidationError("arrow 需要两个不同的有限坐标。")
            }
            try requireWidth(value.lineWidth, name: "lineWidth", maximum: maxLine)
        case .pen(let value), .highlighter(let value):
            try requireID(value.id)
            guard !value.points.isEmpty, value.points.allSatisfy(\.isFinite) else {
                throw AnnotationRecipeValidationError("画笔路径至少需要一个有限坐标。")
            }
            try requirePointCount(value.points.count, name: "points")
            try requireWidth(value.lineWidth, name: "lineWidth", maximum: maxLine)
        case .text(let value):
            try requireID(value.id)
            guard value.origin.isFinite, !value.text.isEmpty else {
                throw AnnotationRecipeValidationError("text 需要有限坐标和非空文字。")
            }
            guard value.text.count <= AnnotationRecipeLimits.maximumTextLength else {
                throw AnnotationRecipeValidationError(
                    "text 最多 \(AnnotationRecipeLimits.maximumTextLength) 个字符。"
                )
            }
            try requireWidth(value.fontSize, name: "fontSize", maximum: AnnotationRecipeLimits.maximumFontSize)
        case .number(let value):
            try requireID(value.id)
            guard value.center.isFinite else {
                throw AnnotationRecipeValidationError("number.center 必须是有限坐标。")
            }
            try requireWidth(value.diameter, name: "diameter", maximum: AnnotationRecipeLimits.maximumDiameter)
        case .mosaic(let value):
            try requireID(value.id)
            try requireWidth(value.scale, name: "mosaic.scale", maximum: AnnotationRecipeLimits.maximumMosaicScale)
            switch value.mode {
            case .rect:
                guard let rect = value.rect, value.points == nil else {
                    throw AnnotationRecipeValidationError("矩形马赛克只接受 rect。")
                }
                try requireRect(rect, name: "mosaic.rect")
            case .brush:
                guard let points = value.points, !points.isEmpty, value.rect == nil,
                      points.allSatisfy(\.isFinite) else {
                    throw AnnotationRecipeValidationError("笔刷马赛克只接受非空 points。")
                }
                try requirePointCount(points.count, name: "mosaic.points")
                try requireWidth(value.lineWidth, name: "mosaic.lineWidth", maximum: maxLine)
            }
        case .blur(let value):
            try requireID(value.id)
            try requireRect(value.rect, name: "blur.rect")
            try requireWidth(value.radius, name: "blur.radius", maximum: AnnotationRecipeLimits.maximumBlurRadius)
        case .magnify(let value):
            try requireID(value.id)
            try requireRect(value.rect, name: "magnify.rect")
            guard value.factor.isFinite, value.factor > 1,
                  value.factor <= AnnotationRecipeLimits.maximumMagnifyFactor else {
                throw AnnotationRecipeValidationError(
                    "magnify.factor 必须大于 1 且不超过 \(Int(AnnotationRecipeLimits.maximumMagnifyFactor))。"
                )
            }
        case .eraser(let value):
            guard !value.targetIDs.isEmpty,
                  value.targetIDs.allSatisfy({ !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }),
                  Set(value.targetIDs).count == value.targetIDs.count else {
                throw AnnotationRecipeValidationError("eraser.targetIds 必须是非空且不重复的 ID 数组。")
            }
        }
    }
}

extension AnnotationOperation: Codable {
    private enum CodingKeys: String, CodingKey { case type }
    private enum OperationType: String, Codable {
        case crop, rectangle, ellipse, arrow, pen, highlighter, text, number, mosaic, blur, magnify, eraser
    }

    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        switch try values.decode(OperationType.self, forKey: .type) {
        case .crop: self = .crop(try AnnotationCropOperation(from: decoder))
        case .rectangle: self = .rectangle(try AnnotationRectOperation(from: decoder))
        case .ellipse: self = .ellipse(try AnnotationRectOperation(from: decoder))
        case .arrow: self = .arrow(try AnnotationArrowOperation(from: decoder))
        case .pen: self = .pen(try AnnotationStrokeOperation(from: decoder))
        case .highlighter:
            let decoded = try AnnotationStrokeOperation(from: decoder)
            let color = try decoder.container(keyedBy: StrokeCodingKeys.self)
                .decodeIfPresent(AnnotationColor.self, forKey: .color) ?? .highlighter
            self = .highlighter(.init(
                id: decoded.id,
                points: decoded.points,
                color: color,
                lineWidth: decoded.lineWidth == 5 ? 16 : decoded.lineWidth,
                dashed: false
            ))
        case .text: self = .text(try AnnotationTextOperation(from: decoder))
        case .number: self = .number(try AnnotationNumberOperation(from: decoder))
        case .mosaic: self = .mosaic(try AnnotationMosaicOperation(from: decoder))
        case .blur: self = .blur(try AnnotationBlurOperation(from: decoder))
        case .magnify: self = .magnify(try AnnotationMagnifyOperation(from: decoder))
        case .eraser: self = .eraser(try AnnotationEraserOperation(from: decoder))
        }
    }

    public func encode(to encoder: Encoder) throws {
        var values = encoder.container(keyedBy: CodingKeys.self)
        switch self {
        case .crop(let value): try values.encode(OperationType.crop, forKey: .type); try value.encode(to: encoder)
        case .rectangle(let value): try values.encode(OperationType.rectangle, forKey: .type); try value.encode(to: encoder)
        case .ellipse(let value): try values.encode(OperationType.ellipse, forKey: .type); try value.encode(to: encoder)
        case .arrow(let value): try values.encode(OperationType.arrow, forKey: .type); try value.encode(to: encoder)
        case .pen(let value): try values.encode(OperationType.pen, forKey: .type); try value.encode(to: encoder)
        case .highlighter(let value): try values.encode(OperationType.highlighter, forKey: .type); try value.encode(to: encoder)
        case .text(let value): try values.encode(OperationType.text, forKey: .type); try value.encode(to: encoder)
        case .number(let value): try values.encode(OperationType.number, forKey: .type); try value.encode(to: encoder)
        case .mosaic(let value): try values.encode(OperationType.mosaic, forKey: .type); try value.encode(to: encoder)
        case .blur(let value): try values.encode(OperationType.blur, forKey: .type); try value.encode(to: encoder)
        case .magnify(let value): try values.encode(OperationType.magnify, forKey: .type); try value.encode(to: encoder)
        case .eraser(let value): try values.encode(OperationType.eraser, forKey: .type); try value.encode(to: encoder)
        }
    }

    private enum StrokeCodingKeys: String, CodingKey { case color }
}
