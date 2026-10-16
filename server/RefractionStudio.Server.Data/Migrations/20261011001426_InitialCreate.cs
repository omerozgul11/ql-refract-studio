using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RefractionStudio.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    security_stamp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    admin_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    entity_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ip_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    company = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    provider_customer_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    component_hashes = table.Column<List<string>>(type: "text[]", nullable: false),
                    machine_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    os_version = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    blocked = table.Column<bool>(type: "boolean", nullable: false),
                    blocked_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_devices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "email_outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    to_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    text_body = table.Column<string>(type: "text", nullable: false),
                    html_body = table.Column<string>(type: "text", nullable: false),
                    template = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    license_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_outbox", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "license_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    license_id = table.Column<Guid>(type: "uuid", nullable: true),
                    trial_id = table.Column<Guid>(type: "uuid", nullable: true),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ip_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_license_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "license_products",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    key_prefix = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    default_edition = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    default_features = table.Column<List<string>>(type: "text[]", nullable: false),
                    trial_days = table.Column<int>(type: "integer", nullable: false),
                    trials_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_license_products", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "webhook_events",
                columns: table => new
                {
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    event_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    details = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_events", x => new { x.provider, x.event_id });
                });

            migrationBuilder.CreateTable(
                name: "licenses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    license_key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key_hint = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    edition = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    seat_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    maintenance_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    features = table.Column<List<string>>(type: "text[]", nullable: false),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    max_transfers_per_30_days = table.Column<int>(type: "integer", nullable: false, defaultValue: 3),
                    offline_days = table.Column<int>(type: "integer", nullable: false, defaultValue: 30)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_licenses", x => x.id);
                    table.CheckConstraint("ck_licenses_seat_count", "seat_count >= 1");
                    table.ForeignKey(
                        name: "fk_licenses_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_licenses_license_products_product",
                        column: x => x.product,
                        principalTable: "license_products",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "software_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    download_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    release_notes = table.Column<string>(type: "text", nullable: false),
                    major = table.Column<bool>(type: "boolean", nullable: false),
                    minimum_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    published = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_software_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_software_versions_license_products_product",
                        column: x => x.product,
                        principalTable: "license_products",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "trial_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    component_hashes = table.Column<List<string>>(type: "text[]", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    extended_by_admin = table.Column<bool>(type: "boolean", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    machine_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    os_version = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    app_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    reminder_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trial_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_trial_devices_license_products_product",
                        column: x => x.product,
                        principalTable: "license_products",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "activations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    license_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_validated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    app_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    deactivation_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_activations", x => x.id);
                    table.ForeignKey(
                        name: "fk_activations_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_activations_licenses_license_id",
                        column: x => x.license_id,
                        principalTable: "licenses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_payment_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    license_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.ForeignKey(
                        name: "fk_payments_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_licenses_license_id",
                        column: x => x.license_id,
                        principalTable: "licenses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_activations_device_id",
                table: "activations",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_activations_license_id_deactivated_at",
                table: "activations",
                columns: new[] { "license_id", "deactivated_at" });

            migrationBuilder.CreateIndex(
                name: "ux_activations_active_license_device",
                table: "activations",
                columns: new[] { "license_id", "device_id" },
                unique: true,
                filter: "deactivated_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_admin_users_email",
                table: "admin_users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_admin_user_id",
                table: "audit_log",
                column: "admin_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entity_type_entity_id",
                table: "audit_log",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_occurred_at",
                table: "audit_log",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_customers_email",
                table: "customers",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_devices_product_fingerprint",
                table: "devices",
                columns: new[] { "product", "fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_email_outbox_license_id",
                table: "email_outbox",
                column: "license_id");

            migrationBuilder.CreateIndex(
                name: "ix_email_outbox_status_next_attempt_at",
                table: "email_outbox",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_license_events_device_id_occurred_at",
                table: "license_events",
                columns: new[] { "device_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_license_events_license_id_occurred_at",
                table: "license_events",
                columns: new[] { "license_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_license_events_occurred_at",
                table: "license_events",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_license_events_trial_id_occurred_at",
                table: "license_events",
                columns: new[] { "trial_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_licenses_customer_id",
                table: "licenses",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_licenses_key_hint",
                table: "licenses",
                column: "key_hint");

            migrationBuilder.CreateIndex(
                name: "ix_licenses_license_key_hash",
                table: "licenses",
                column: "license_key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_licenses_product",
                table: "licenses",
                column: "product");

            migrationBuilder.CreateIndex(
                name: "ix_licenses_status_expires_at",
                table: "licenses",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_customer_id",
                table: "payments",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_payments_license_id",
                table: "payments",
                column: "license_id");

            migrationBuilder.CreateIndex(
                name: "ix_payments_provider_provider_payment_id",
                table: "payments",
                columns: new[] { "provider", "provider_payment_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_software_versions_product_channel_published",
                table: "software_versions",
                columns: new[] { "product", "channel", "published" });

            migrationBuilder.CreateIndex(
                name: "ix_software_versions_product_version",
                table: "software_versions",
                columns: new[] { "product", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_trial_devices_component_hashes",
                table: "trial_devices",
                column: "component_hashes")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_trial_devices_ends_at",
                table: "trial_devices",
                column: "ends_at");

            migrationBuilder.CreateIndex(
                name: "ix_trial_devices_product_fingerprint",
                table: "trial_devices",
                columns: new[] { "product", "fingerprint" });

            migrationBuilder.Sql(@"
INSERT INTO license_products (code, name, key_prefix, default_edition, default_features, trial_days, trials_enabled, active)
VALUES ('refraction-studio', 'Refraction Studio', 'RFST', 'Professional', '{}', 14, TRUE, TRUE)
ON CONFLICT (code) DO NOTHING;
");

            // license_events and audit_log are append-only: reject UPDATE, DELETE and TRUNCATE at the database level.
            migrationBuilder.Sql(@"
CREATE FUNCTION reject_append_only_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% is append-only', TG_TABLE_NAME USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER license_events_append_only BEFORE UPDATE OR DELETE ON license_events FOR EACH ROW EXECUTE FUNCTION reject_append_only_change();
CREATE TRIGGER license_events_no_truncate BEFORE TRUNCATE ON license_events FOR EACH STATEMENT EXECUTE FUNCTION reject_append_only_change();
CREATE TRIGGER audit_log_append_only BEFORE UPDATE OR DELETE ON audit_log FOR EACH ROW EXECUTE FUNCTION reject_append_only_change();
CREATE TRIGGER audit_log_no_truncate BEFORE TRUNCATE ON audit_log FOR EACH STATEMENT EXECUTE FUNCTION reject_append_only_change();
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS license_events_append_only ON license_events;
DROP TRIGGER IF EXISTS license_events_no_truncate ON license_events;
DROP TRIGGER IF EXISTS audit_log_append_only ON audit_log;
DROP TRIGGER IF EXISTS audit_log_no_truncate ON audit_log;
DROP FUNCTION IF EXISTS reject_append_only_change();
");

            migrationBuilder.DropTable(
                name: "activations");

            migrationBuilder.DropTable(
                name: "admin_users");

            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "email_outbox");

            migrationBuilder.DropTable(
                name: "license_events");

            migrationBuilder.DropTable(
                name: "payments");

            migrationBuilder.DropTable(
                name: "software_versions");

            migrationBuilder.DropTable(
                name: "trial_devices");

            migrationBuilder.DropTable(
                name: "webhook_events");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "licenses");

            migrationBuilder.DropTable(
                name: "customers");

            migrationBuilder.DropTable(
                name: "license_products");
        }
    }
}
