using System.Text;
using System.Text.Json;
using Gozleme.Api.Manifests;
using Gozleme.Api.Manifests.Parsing;

namespace Gozleme.Api.Tests.Manifests;

/// <summary>
/// GOZ-0006 research R3 (FR-012, FR-016): JSON, restricted YAML and SKILL.md all parse into one positioned tree with
/// RFC 6901 pointers; every refused construct is <c>malformed</c> with a line and column.
/// </summary>
public sealed class ManifestParserTests
{
    private static ManifestNode Json(string text) => JsonTreeReader.Read(Encoding.UTF8.GetBytes(text), ManifestPart.Native, null, out _);

    private static ManifestProblem JsonFails(string text) => Assert.Throws<ValidationStop>(() => Json(text)).Problem;

    private static ManifestProblem YamlFails(string text) => Assert.Throws<ValidationStop>(() => RestrictedYamlReader.Read(text, ManifestPart.Native)).Problem;

    private static string Where(ManifestProblem problem) => $"{problem.Rule.Id} {ManifestTestKit.Where(problem.Location)}";

    [Fact]
    public void The_json_reader_has_no_duplicate_property_option_so_detection_is_ours() =>
        Assert.Null(typeof(JsonReaderOptions).GetProperty("AllowDuplicateProperties"));

    [Theory]
    [InlineData("""{"a":1,"a":2}""", "parse.duplicate-key native 1:8")]
    [InlineData("{\"a\":{\"b\":1,\n\"b\":2}}", "parse.duplicate-key native 2:1")]
    [InlineData("""[{"x":1},{"x":1,"y":{"z":0,"z":0}}]""", "parse.duplicate-key native 1:28")]
    [InlineData("""{"a":1} // c""", "parse.syntax native 1:9")]
    [InlineData("""{"a":1,}""", "parse.syntax native 1:8")]
    [InlineData("{\"a\":\"\u0001\"}", "parse.encoding native 1:7")]
    [InlineData("\u0007{}", "parse.encoding native 1:1")]
    [InlineData("""{"a":"\ud800"}""", "parse.encoding native 1:6")]
    [InlineData("   ", "parse.empty native 1:1")]
    public void Refused_json_is_malformed_at_its_position(string text, string expected) => Assert.Equal(expected, Where(JsonFails(text)));

    [Fact]
    public void A_leading_bom_is_skipped_by_the_parser_and_does_not_count_as_a_column()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("""{"a": }""")).ToArray();
        var problem = Assert.Throws<ValidationStop>(() => JsonTreeReader.Read(bytes, ManifestPart.Native, null, out _)).Problem;
        Assert.Equal("parse.syntax native 1:7", Where(problem));
        Assert.Equal("1", JsonTreeReader.Read(Encoding.UTF8.GetPreamble().Concat("{\"a\":1}"u8.ToArray()).ToArray(), "native", null, out _)["a"]!.Text);
    }

    [Fact]
    public void Pointers_escape_tilde_and_slash_and_positions_count_scalars()
    {
        var tree = Json("{\"a/b\":{\"c~d\":[true,\"😀\",3]},\n  \"é\":null}");
        var array = tree["a/b"]!["c~d"]!;
        Assert.Equal("/a~1b/c~0d", array.Pointer.ToString());
        Assert.Equal("/a~1b/c~0d/2", array.Items[2].Pointer.ToString());
        Assert.Equal((1, 25), (array.Items[2].Line, array.Items[2].Column)); // the emoji is one column
        Assert.Equal((2, 7), (tree["é"]!.Line, tree["é"]!.Column));
    }

    [Theory]
    [InlineData("a: &x 1\nb: 2\n", "parse.yaml-feature native 1:4")]
    [InlineData("a: 1\nb: *x\n", "parse.yaml-feature native 2:4")]
    [InlineData("a: &x 1\nb: *x\n", "parse.yaml-feature native 1:4")]
    [InlineData("a: !!str 1\n", "parse.yaml-feature native 1:4")]
    [InlineData("a: !custom 1\n", "parse.yaml-feature native 1:4")]
    [InlineData("<<: {a: 1}\n", "parse.yaml-feature native 1:1")]
    [InlineData("a: 1\n---\nb: 2\n", "parse.yaml-feature native 2:1")]
    [InlineData("? [a]\n: 1\n", "parse.yaml-feature native 1:3")]
    [InlineData("a: 1\nb: 2\na: 3\n", "parse.duplicate-key native 3:1")]
    [InlineData("a: [1, 2\n", "parse.syntax native 2:1")]
    [InlineData("a: \"\u0001\"\n", "parse.encoding native 1:5")]
    public void Refused_yaml_is_malformed_at_its_position(string text, string expected) => Assert.Equal(expected, Where(YamlFails(text)));

    [Fact]
    public void Plain_scalars_resolve_with_the_yaml_1_2_core_schema()
    {
        var tree = RestrictedYamlReader.Read("a: yes\nb: ~\nc: \nd: TRUE\ne: 0x1F\nf: 0o17\ng: +12\nh: .5\ni: 1.\nj: 1e3\nk: '1'\nl: on\nm: .inf\n200: x\n", "native")!;
        string Kind(string name) => $"{tree[name]!.Kind}:{tree[name]!.Text ?? tree[name]!.Boolean.ToString()}";
        Assert.Equal("String:yes", Kind("a"));
        Assert.Equal(NodeKind.Null, tree["b"]!.Kind);
        Assert.Equal(NodeKind.Null, tree["c"]!.Kind);
        Assert.Equal("Boolean:True", Kind("d"));
        Assert.Equal("Number:31", Kind("e"));
        Assert.Equal("Number:15", Kind("f"));
        Assert.Equal("Number:12", Kind("g"));
        Assert.Equal("Number:0.5", Kind("h"));
        Assert.Equal("Number:1", Kind("i"));
        Assert.Equal("Number:1e3", Kind("j"));
        Assert.Equal("String:1", Kind("k"));
        Assert.Equal("String:on", Kind("l"));
        Assert.Equal("String:.inf", Kind("m"));
        Assert.Equal("String:x", Kind("200"));
        Assert.Equal("""{"a":"yes","b":null,"c":null,"d":true,"e":31,"f":15,"g":12,"h":0.5,"i":1,"j":1e3,"k":"1","l":"on","m":".inf","200":"x"}""",
            Encoding.UTF8.GetString(tree.ToJson()));
    }

    [Fact]
    public void Yaml_and_json_give_the_same_pointers_and_yaml_positions_are_lines_and_columns()
    {
        var tree = RestrictedYamlReader.Read("paths:\n  /a~b:\n    get:\n      summary: x\n", "native")!;
        var summary = tree["paths"]!["/a~b"]!["get"]!["summary"]!;
        Assert.Equal("/paths/~1a~0b/get/summary", summary.Pointer.ToString());
        Assert.Equal((4, 16), (summary.Line, summary.Column));
    }

    [Theory]
    [InlineData("# no front matter\n", "parse.front-matter native 1:1")]
    [InlineData("---\nname: x\n", "parse.front-matter native 1:1")]
    [InlineData("\n---\nname: x\n---\n", "parse.front-matter native 1:1")]
    [InlineData("---\nname: &a x\n---\n", "parse.yaml-feature native 2:7")]
    public void Skill_front_matter_must_be_a_closed_block(string text, string expected) =>
        Assert.Equal(expected, Where(Assert.Throws<ValidationStop>(() => SkillMarkdownReader.Read(text, "native")).Problem));

    [Fact]
    public void Skill_body_lines_keep_their_numbers()
    {
        var skill = SkillMarkdownReader.Read("\uFEFF---\r\nname: demo\r\ndescription: d\r\n---\r\n# Title\r\nline two\n", "native");
        Assert.Equal("demo", skill.FrontMatter.StringOf("name"));
        Assert.Equal((2, 7), (skill.FrontMatter["name"]!.Line, skill.FrontMatter["name"]!.Column));
        Assert.Equal([(5, "# Title"), (6, "line two"), (7, "")], skill.Body.Select(l => (l.Number, l.Text)));
    }
}
