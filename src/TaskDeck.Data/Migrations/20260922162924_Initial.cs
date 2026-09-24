using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskDeck.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppState",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppState", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Holiday",
                columns: table => new
                {
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    LocalName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CountryCode = table.Column<string>(type: "TEXT", maxLength: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Holiday", x => x.Date);
                });

            migrationBuilder.CreateTable(
                name: "LinkPreview",
                columns: table => new
                {
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkPreview", x => x.Url);
                });

            migrationBuilder.CreateTable(
                name: "Project",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    IconKey = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    SortOrder = table.Column<double>(type: "REAL", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SyncState = table.Column<int>(type: "INTEGER", nullable: false),
                    RemoteUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Project", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecurrenceRule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RRule = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    AnchorAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndKind = table.Column<int>(type: "INTEGER", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    MaxOccurrences = table.Column<int>(type: "INTEGER", nullable: true),
                    CompletedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseKind = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SyncState = table.Column<int>(type: "INTEGER", nullable: false),
                    RemoteUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurrenceRule", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncMeta",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncCursor = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncMeta", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tag",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "NOCASE"),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SyncState = table.Column<int>(type: "INTEGER", nullable: false),
                    RemoteUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tag", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WeatherCache",
                columns: table => new
                {
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    WeatherCode = table.Column<int>(type: "INTEGER", nullable: false),
                    TemperatureMax = table.Column<double>(type: "REAL", nullable: false),
                    TemperatureMin = table.Column<double>(type: "REAL", nullable: false),
                    PrecipitationProbability = table.Column<int>(type: "INTEGER", nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeatherCache", x => x.Date);
                });

            migrationBuilder.CreateTable(
                name: "TaskTemplate",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IconKey = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 7, nullable: true),
                    AnchorLabel = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    DefaultProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DefaultTagIds = table.Column<string>(type: "TEXT", nullable: false),
                    UseCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SyncState = table.Column<int>(type: "INTEGER", nullable: false),
                    RemoteUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskTemplate", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskTemplate_Project_DefaultProjectId",
                        column: x => x.DefaultProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TaskItem",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    DueAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DueHasTime = table.Column<bool>(type: "INTEGER", nullable: false),
                    RemindAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RemindOffsetMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    NotifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ParentTaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Depth = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RecurrenceRuleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RecurrenceSeriesId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DurationMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    TemplateBatchId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SyncState = table.Column<int>(type: "INTEGER", nullable: false),
                    RemoteUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SearchKey = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskItem", x => x.Id);
                    table.CheckConstraint("CK_TaskItem_Depth", "\"Depth\" >= 0 AND \"Depth\" <= 2");
                    table.ForeignKey(
                        name: "FK_TaskItem_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TaskItem_RecurrenceRule_RecurrenceRuleId",
                        column: x => x.RecurrenceRuleId,
                        principalTable: "RecurrenceRule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TaskItem_TaskItem_ParentTaskId",
                        column: x => x.ParentTaskId,
                        principalTable: "TaskItem",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TaskTemplateItem",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TemplateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    DueOffsetDays = table.Column<int>(type: "INTEGER", nullable: true),
                    DueTime = table.Column<string>(type: "TEXT", maxLength: 5, nullable: true),
                    RemindOffsetMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    ParentItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Depth = table.Column<int>(type: "INTEGER", nullable: false),
                    TagIds = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SyncState = table.Column<int>(type: "INTEGER", nullable: false),
                    RemoteUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskTemplateItem", x => x.Id);
                    table.CheckConstraint("CK_TaskTemplateItem_Depth", "\"Depth\" >= 0 AND \"Depth\" <= 2");
                    table.ForeignKey(
                        name: "FK_TaskTemplateItem_TaskTemplateItem_ParentItemId",
                        column: x => x.ParentItemId,
                        principalTable: "TaskTemplateItem",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TaskTemplateItem_TaskTemplate_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "TaskTemplate",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskTag",
                columns: table => new
                {
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TagId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SyncState = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskTag", x => new { x.TaskId, x.TagId });
                    table.ForeignKey(
                        name: "FK_TaskTag_Tag_TagId",
                        column: x => x.TagId,
                        principalTable: "Tag",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaskTag_TaskItem_TaskId",
                        column: x => x.TaskId,
                        principalTable: "TaskItem",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Project_Order",
                table: "Project",
                columns: new[] { "DeletedAt", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "UX_Tag_Name",
                table: "Tag",
                column: "Name",
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Task_Batch",
                table: "TaskItem",
                column: "TemplateBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_Task_Completed",
                table: "TaskItem",
                columns: new[] { "Status", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Task_Due",
                table: "TaskItem",
                columns: new[] { "DeletedAt", "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Task_Parent",
                table: "TaskItem",
                column: "ParentTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Task_Project",
                table: "TaskItem",
                columns: new[] { "ProjectId", "DeletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Task_Remind",
                table: "TaskItem",
                column: "RemindAt",
                filter: "\"NotifiedAt\" IS NULL AND \"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Task_Series",
                table: "TaskItem",
                column: "RecurrenceSeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_Task_Sync",
                table: "TaskItem",
                column: "SyncState");

            migrationBuilder.CreateIndex(
                name: "IX_TaskItem_RecurrenceRuleId",
                table: "TaskItem",
                column: "RecurrenceRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskTag_Tag",
                table: "TaskTag",
                columns: new[] { "TagId", "DeletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskTemplate_DefaultProjectId",
                table: "TaskTemplate",
                column: "DefaultProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Template_Use",
                table: "TaskTemplate",
                columns: new[] { "DeletedAt", "UseCount" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_TaskTemplateItem_ParentItemId",
                table: "TaskTemplateItem",
                column: "ParentItemId");

            migrationBuilder.CreateIndex(
                name: "IX_TemplateItem_Tpl",
                table: "TaskTemplateItem",
                columns: new[] { "TemplateId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppState");

            migrationBuilder.DropTable(
                name: "Holiday");

            migrationBuilder.DropTable(
                name: "LinkPreview");

            migrationBuilder.DropTable(
                name: "SyncMeta");

            migrationBuilder.DropTable(
                name: "TaskTag");

            migrationBuilder.DropTable(
                name: "TaskTemplateItem");

            migrationBuilder.DropTable(
                name: "WeatherCache");

            migrationBuilder.DropTable(
                name: "Tag");

            migrationBuilder.DropTable(
                name: "TaskItem");

            migrationBuilder.DropTable(
                name: "TaskTemplate");

            migrationBuilder.DropTable(
                name: "RecurrenceRule");

            migrationBuilder.DropTable(
                name: "Project");
        }
    }
}
