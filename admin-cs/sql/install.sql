-- Also executed automatically by the resource on start (CREATE TABLE IF NOT EXISTS).
CREATE TABLE IF NOT EXISTS admins (
    license    VARCHAR(64) PRIMARY KEY,
    role       TINYINT     NOT NULL,              -- 1 = mod, 2 = admin, 3 = superadmin
    name       VARCHAR(64) NOT NULL,
    updated_at TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS bans (
    id         INT AUTO_INCREMENT PRIMARY KEY,
    license    VARCHAR(64)  NOT NULL,
    name       VARCHAR(64)  NOT NULL,
    reason     VARCHAR(255) NOT NULL,
    banned_by  VARCHAR(64)  NOT NULL,             -- admin name
    created_at TIMESTAMP    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    expires_at TIMESTAMP    NULL DEFAULT NULL,    -- NULL = permanent
    active     TINYINT      NOT NULL DEFAULT 1,
    INDEX idx_license (license, active)
);

CREATE TABLE IF NOT EXISTS admin_log (
    id            INT AUTO_INCREMENT PRIMARY KEY,
    admin_license VARCHAR(64)  NOT NULL,
    admin_name    VARCHAR(64)  NOT NULL,
    action        VARCHAR(32)  NOT NULL,
    target        VARCHAR(64)  NULL,
    details       VARCHAR(255) NULL,
    created_at    TIMESTAMP    NOT NULL DEFAULT CURRENT_TIMESTAMP
);
