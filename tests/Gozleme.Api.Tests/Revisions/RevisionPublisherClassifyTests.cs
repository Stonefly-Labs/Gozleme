using Gozleme.Api.Revisions;
using Npgsql;

namespace Gozleme.Api.Tests.Revisions;

/// <summary>
/// T083 (maintainer ruling F4): the defence-in-depth answers of <see cref="RevisionPublisher.Classify"/>. A child-table
/// refusal is <c>unavailable</c>, including a row-level <c>WITH CHECK</c> refusal that carries no table name while the
/// children are recorded; the same refusal of the revision insert itself stays <c>suspended</c>.
/// </summary>
public sealed class RevisionPublisherClassifyTests
{
    private static PostgresException Pg(string sqlState, string? table = null, string? constraint = null) =>
        new("refused", "ERROR", "ERROR", sqlState, tableName: table, constraintName: constraint);

    [Theory]
    [InlineData("capability_children")]
    [InlineData("revision_children")]
    [InlineData("projection_generations")]
    public void A_child_table_42501_or_23505_is_unavailable(string table)
    {
        Assert.Equal(PublishResult.Unavailable, RevisionPublisher.Classify(Pg(PostgresErrorCodes.InsufficientPrivilege, table)));
        Assert.Equal(PublishResult.Unavailable, RevisionPublisher.Classify(Pg(PostgresErrorCodes.UniqueViolation, table)));
    }

    [Fact]
    public void A_null_table_42501_is_unavailable_while_recording_children_and_suspended_otherwise()
    {
        Assert.Equal(PublishResult.Unavailable, RevisionPublisher.Classify(Pg(PostgresErrorCodes.InsufficientPrivilege), recordingChildren: true));
        Assert.Equal(PublishResult.Suspended, RevisionPublisher.Classify(Pg(PostgresErrorCodes.InsufficientPrivilege)));
        Assert.Equal(PublishResult.Suspended, RevisionPublisher.Classify(Pg(PostgresErrorCodes.InsufficientPrivilege, "manifest_revisions")));
    }

    [Theory]
    [InlineData("capability_children_recorded")]
    [InlineData("manifest_revisions_children")]
    public void A_completeness_23000_is_unavailable(string constraint) =>
        Assert.Equal(PublishResult.Unavailable, RevisionPublisher.Classify(Pg(PostgresErrorCodes.IntegrityConstraintViolation, constraint: constraint)));

    [Theory]
    [InlineData(PostgresErrorCodes.UntranslatableCharacter)]
    [InlineData(PostgresErrorCodes.CharacterNotInRepertoire)]
    public void Text_the_database_cannot_hold_is_unavailable(string sqlState) =>
        Assert.Equal(PublishResult.Unavailable, RevisionPublisher.Classify(Pg(sqlState)));

    [Fact]
    public void A_revision_key_collision_is_version_exists_and_anything_else_is_not_classified()
    {
        Assert.Equal(PublishResult.VersionExists, RevisionPublisher.Classify(Pg(PostgresErrorCodes.UniqueViolation, "manifest_revisions")));
        Assert.Equal(PublishResult.VersionExists, RevisionPublisher.Classify(new InvalidOperationException("wrapped", Pg(PostgresErrorCodes.UniqueViolation, "manifest_revisions"))));
        Assert.Null(RevisionPublisher.Classify(Pg(PostgresErrorCodes.IntegrityConstraintViolation, constraint: "other")));
        Assert.Null(RevisionPublisher.Classify(Pg(PostgresErrorCodes.SyntaxError)));
        Assert.Null(RevisionPublisher.Classify(new InvalidOperationException("not a database refusal")));
    }
}
