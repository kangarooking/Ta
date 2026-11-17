import Foundation
import Testing
@testable import TaAgentContracts

@Suite("Annotation recipe v1")
struct AnnotationRecipeTests {
    @Test("decodes every supported operation and both color formats")
    func decodesEveryOperation() throws {
        let json = #"""
        {
          "version": 1,
          "operations": [
            {"type":"crop","rect":{"x":10,"y":20,"width":600,"height":400}},
            {"type":"rectangle","id":"rect","rect":{"x":20,"y":30,"width":120,"height":80},"color":"#FF3B30","lineWidth":6,"dashed":true},
            {"type":"ellipse","id":"ellipse","rect":{"x":180,"y":30,"width":100,"height":80},"color":"#34C759CC"},
            {"type":"arrow","id":"arrow","start":{"x":20,"y":160},"end":{"x":180,"y":120}},
            {"type":"pen","id":"pen","points":[{"x":20,"y":200},{"x":80,"y":240}]},
            {"type":"highlighter","id":"highlight","points":[{"x":100,"y":200},{"x":220,"y":200}]},
            {"type":"text","id":"text","origin":{"x":40,"y":280},"text":"重点内容","fontSize":28},
            {"type":"number","id":"number","center":{"x":300,"y":100},"number":1,"diameter":32},
            {"type":"mosaic","id":"mosaic-box","mode":"rect","rect":{"x":300,"y":180,"width":80,"height":50}},
            {"type":"mosaic","id":"mosaic-brush","mode":"brush","points":[{"x":300,"y":260},{"x":380,"y":280}],"lineWidth":24},
            {"type":"blur","id":"blur","rect":{"x":420,"y":40,"width":100,"height":60},"radius":12},
            {"type":"magnify","id":"magnify","rect":{"x":420,"y":160,"width":100,"height":100},"factor":2},
            {"type":"eraser","targetIds":["pen"]}
          ]
        }
        """#

        let recipe = try AgentJSONCoding.decoder().decode(AnnotationRecipe.self, from: Data(json.utf8))
        let validated = try recipe.validated()

        #expect(validated.operations.count == 13)
        #expect(validated.resultingElementIDs.contains("rect"))
        #expect(!validated.resultingElementIDs.contains("pen"))
        guard case .rectangle(let rectangle) = validated.operations[1] else {
            Issue.record("expected rectangle operation")
            return
        }
        #expect(rectangle.color == AnnotationColor(red: 1, green: 59.0 / 255, blue: 48.0 / 255, alpha: 1))
        guard case .ellipse(let ellipse) = validated.operations[2] else {
            Issue.record("expected ellipse operation")
            return
        }
        #expect(abs(ellipse.color.alpha - 0.8) < 0.001)
    }

    @Test("rejects unsupported recipe versions")
    func rejectsUnsupportedVersion() throws {
        let recipe = AnnotationRecipe(version: 2, operations: [])
        #expect(throws: AnnotationRecipeValidationError.self) {
            _ = try recipe.validated()
        }
    }

    @Test("rejects crop after another operation")
    func cropMustBeFirst() throws {
        let recipe = AnnotationRecipe(version: 1, operations: [
            .rectangle(.init(id: "one", rect: .init(x: 1, y: 1, width: 10, height: 10))),
            .crop(.init(rect: .init(x: 0, y: 0, width: 20, height: 20)))
        ])

        #expect(throws: AnnotationRecipeValidationError.self) {
            _ = try recipe.validated()
        }
    }

    @Test("rejects duplicate IDs, empty paths, and invalid geometry")
    func rejectsInvalidOperations() throws {
        let recipes = [
            AnnotationRecipe(version: 1, operations: [
                .rectangle(.init(id: "same", rect: .init(x: 0, y: 0, width: 10, height: 10))),
                .ellipse(.init(id: "same", rect: .init(x: 20, y: 20, width: 10, height: 10)))
            ]),
            AnnotationRecipe(version: 1, operations: [
                .pen(.init(id: "empty", points: []))
            ]),
            AnnotationRecipe(version: 1, operations: [
                .blur(.init(id: "bad", rect: .init(x: 0, y: 0, width: 0, height: 10)))
            ])
        ]

        for recipe in recipes {
            #expect(throws: AnnotationRecipeValidationError.self) {
                _ = try recipe.validated()
            }
        }
    }

    @Test("rejects absurdly large sizes that would overflow renderer integer conversions")
    func rejectsOversizedValues() throws {
        let rect = AnnotationRect(x: 0, y: 0, width: 10, height: 10)
        let operations: [AnnotationOperation] = [
            .blur(.init(id: "blur", rect: rect, radius: 1e30)),
            .mosaic(.init(id: "mosaic", mode: .rect, rect: rect, scale: 1e30)),
            .mosaic(.init(id: "brush", mode: .brush, points: [.init(x: 1, y: 1)], lineWidth: 1e30)),
            .rectangle(.init(id: "rect", rect: rect, lineWidth: 1e30)),
            .rectangle(.init(id: "huge", rect: .init(x: 0, y: 0, width: 1e30, height: 10))),
            .text(.init(id: "text", origin: .init(x: 0, y: 0), text: "a", fontSize: 1e30)),
            .number(.init(id: "number", center: .init(x: 0, y: 0), number: 1, diameter: 1e30)),
            .magnify(.init(id: "magnify", rect: rect, factor: 1e30)),
            .pen(.init(id: "pen", points: [.init(x: 1e30, y: 0)])),
            .blur(.init(id: "inf", rect: rect, radius: .infinity))
        ]

        for operation in operations {
            #expect(throws: AnnotationRecipeValidationError.self) {
                _ = try AnnotationRecipe(version: 1, operations: [operation]).validated()
            }
        }
        // Values at the documented limits stay valid.
        _ = try AnnotationRecipe(version: 1, operations: [
            .blur(.init(id: "blur", rect: rect, radius: AnnotationRecipeLimits.maximumBlurRadius)),
            .mosaic(.init(id: "mosaic", mode: .rect, rect: rect, scale: AnnotationRecipeLimits.maximumMosaicScale))
        ]).validated()
    }

    @Test("eraser resolves IDs from the existing session and current recipe")
    func eraserUsesStableIDs() throws {
        let recipe = AnnotationRecipe(version: 1, operations: [
            .arrow(.init(
                id: "new-arrow",
                start: .init(x: 0, y: 0),
                end: .init(x: 20, y: 20)
            )),
            .eraser(.init(targetIDs: ["old-label", "new-arrow"]))
        ])

        let validated = try recipe.validated(existingElementIDs: ["old-label"])
        #expect(validated.resultingElementIDs.isEmpty)

        #expect(throws: AnnotationRecipeValidationError.self) {
            _ = try recipe.validated(existingElementIDs: [])
        }
    }

    @Test("checked-in v1 fixture decodes and validates")
    func fixtureValidates() throws {
        let fixtureURL = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("Fixtures/annotation-recipe-v1.json")
        let recipe = try AgentJSONCoding.decoder().decode(
            AnnotationRecipe.self,
            from: Data(contentsOf: fixtureURL)
        )

        let validated = try recipe.validated()
        #expect(validated.operations.count == 14)
        #expect(validated.resultingElementIDs.count == 11)
    }
}
