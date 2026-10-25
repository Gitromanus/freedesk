-- FreeDesk signaling, схема v1.
-- MySQL 5.7+ / utf8mb4.

CREATE TABLE IF NOT EXISTS devices (
  id          INT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id   CHAR(9)      NOT NULL COMMENT '9-значный адрес устройства',
  token_hash  CHAR(64)     NOT NULL COMMENT 'sha256(device_token)',
  name        VARCHAR(64)  NOT NULL,
  os          VARCHAR(32)  NOT NULL DEFAULT 'unknown',
  pubkey      VARCHAR(512) NULL,
  last_seen   DATETIME     NULL,
  created_at  DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (id),
  UNIQUE KEY uq_public_id (public_id),
  UNIQUE KEY uq_token_hash (token_hash),
  KEY idx_last_seen (last_seen)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS sessions (
  id          CHAR(32)     NOT NULL COMMENT 'hex(random_bytes(16))',
  from_device INT UNSIGNED NOT NULL COMMENT 'инициатор',
  to_device   INT UNSIGNED NOT NULL COMMENT 'принимающая сторона',
  state       ENUM('pending','active','ended') NOT NULL DEFAULT 'pending',
  created_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  accepted_at DATETIME NULL,
  ended_at    DATETIME NULL,
  PRIMARY KEY (id),
  KEY idx_from (from_device),
  KEY idx_to (to_device),
  KEY idx_state (state, created_at),
  CONSTRAINT fk_sessions_from FOREIGN KEY (from_device) REFERENCES devices (id) ON DELETE CASCADE,
  CONSTRAINT fk_sessions_to   FOREIGN KEY (to_device)   REFERENCES devices (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS signals (
  id         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  session_id CHAR(32)        NOT NULL,
  from_device INT UNSIGNED   NOT NULL,
  type       VARCHAR(32)     NOT NULL COMMENT 'offer | answer | ice | bye | ...',
  payload    MEDIUMTEXT      NULL COMMENT 'JSON',
  created_at DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (id),
  KEY idx_session (session_id, id),
  CONSTRAINT fk_signals_session FOREIGN KEY (session_id) REFERENCES sessions (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS state (
  k VARCHAR(64)  NOT NULL,
  v VARCHAR(255) NOT NULL,
  PRIMARY KEY (k)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS rate_limits (
  k            VARCHAR(128) NOT NULL,
  window_start INT UNSIGNED NOT NULL,
  hits         INT UNSIGNED NOT NULL DEFAULT 0,
  PRIMARY KEY (k)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Заготовка под аккаунты: API v1 её пока не использует.
CREATE TABLE IF NOT EXISTS users (
  id           INT UNSIGNED NOT NULL AUTO_INCREMENT,
  email        VARCHAR(190) NOT NULL,
  pass_hash    VARCHAR(255) NOT NULL,
  totp_secret  VARCHAR(64)  NULL,
  created_at   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (id),
  UNIQUE KEY uq_email (email)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Привязка устройства к аккаунту (для адресной книги и unattended-доступа).
CREATE TABLE IF NOT EXISTS user_devices (
  user_id   INT UNSIGNED NOT NULL,
  device_id INT UNSIGNED NOT NULL,
  role      ENUM('owner','guest') NOT NULL DEFAULT 'owner',
  PRIMARY KEY (user_id, device_id),
  CONSTRAINT fk_ud_user   FOREIGN KEY (user_id)   REFERENCES users (id)   ON DELETE CASCADE,
  CONSTRAINT fk_ud_device FOREIGN KEY (device_id) REFERENCES devices (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
