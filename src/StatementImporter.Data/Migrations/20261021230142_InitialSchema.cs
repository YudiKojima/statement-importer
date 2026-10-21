using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace StatementImporter.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "imports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    file_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    file_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    total_lines = table.Column<int>(type: "integer", nullable: false),
                    total_chunks = table.Column<int>(type: "integer", nullable: false),
                    processed_chunks = table.Column<int>(type: "integer", nullable: false),
                    processed_lines = table.Column<int>(type: "integer", nullable: false),
                    valid_count = table.Column<int>(type: "integer", nullable: false),
                    error_count = table.Column<int>(type: "integer", nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_imports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "import_chunks",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    import_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chunk_index = table.Column<int>(type: "integer", nullable: false),
                    start_offset = table.Column<long>(type: "bigint", nullable: false),
                    first_line_number = table.Column<int>(type: "integer", nullable: false),
                    line_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_chunks", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_chunks_imports_import_id",
                        column: x => x.import_id,
                        principalTable: "imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "import_errors",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    import_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    raw_line = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_errors", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_errors_imports_import_id",
                        column: x => x.import_id,
                        principalTable: "imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "transactions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    transaction_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    import_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transactions", x => x.id);
                    table.ForeignKey(
                        name: "fk_transactions_imports_import_id",
                        column: x => x.import_id,
                        principalTable: "imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_import_chunks_import_id_chunk_index",
                table: "import_chunks",
                columns: new[] { "import_id", "chunk_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_import_errors_import_id_line_number",
                table: "import_errors",
                columns: new[] { "import_id", "line_number" });

            migrationBuilder.CreateIndex(
                name: "ix_imports_file_hash",
                table: "imports",
                column: "file_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_imports_status_created_at",
                table: "imports",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_transactions_import_id_line_number",
                table: "transactions",
                columns: new[] { "import_id", "line_number" });

            migrationBuilder.CreateIndex(
                name: "ix_transactions_transaction_id",
                table: "transactions",
                column: "transaction_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "import_chunks");

            migrationBuilder.DropTable(
                name: "import_errors");

            migrationBuilder.DropTable(
                name: "transactions");

            migrationBuilder.DropTable(
                name: "imports");
        }
    }
}
