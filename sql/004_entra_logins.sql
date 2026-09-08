-- Call Prep 004: sign-in moves from Windows account names to the Microsoft 365 identity (Entra ID).
-- callprep.user_access.login now holds the M365 UPN / email, lower-case (what the id_token's preferred_username claim carries).
-- Rule: one employee sign-on for every internal app (feedback_one_employee_sign_on). Two mail domains exist:
-- raritangroup.com and raritanvalve.com — never assume one.
-- Additive + idempotent. Run as postgres:  sudo -u postgres psql -d reportsdb -v ON_ERROR_STOP=1 -f 004_entra_logins.sql

BEGIN;

-- 1. rename the seed rows from 003 (Windows sAMAccountName -> M365 UPN). Skips any that were already changed.
UPDATE callprep.user_access SET login = m.upn
FROM (VALUES
  ('it',          'it@raritangroup.com'),
  ('pfernandes',  'paul@raritangroup.com'),
  ('jcook',       'joel@raritanvalve.com'),
  ('brichardson', 'bill@raritanvalve.com'),
  ('jrichardson', 'jim@raritangroup.com'),
  ('trichardson', 'tom@raritangroup.com'),
  ('ddickman',    'ddickman@raritangroup.com'),
  ('ftenerovich', 'ftenerovich@raritangroup.com'),
  ('kperry',      'kperry@raritangroup.com'),
  ('tennis',      'tim@raritangroup.com'),
  ('prichardson', 'patrick@raritangroup.com')
) AS m(old, upn)
WHERE callprep.user_access.login = m.old
  AND NOT EXISTS (SELECT 1 FROM callprep.user_access x WHERE x.login = m.upn);

-- 2. reps that have an M365 mailbox but no on-prem domain account (were unmapped in 003)
INSERT INTO callprep.user_access (login, display_name, role, salesrep_id, enabled, notes) VALUES
  ('doug@raritangroup.com',     'Doug Miller',  'rep', '1025',  true, 'P21 rep Douglas Miller'),
  ('jconvery@raritangroup.com', 'John Convery', 'rep', '20992', true, 'P21 rep JOHN CONVERY')
ON CONFLICT (login) DO NOTHING;

-- 3. logins must look like an email from here on (the internal service row is the one exception)
ALTER TABLE callprep.user_access DROP CONSTRAINT IF EXISTS user_access_login_is_email;
ALTER TABLE callprep.user_access ADD CONSTRAINT user_access_login_is_email
  CHECK (login = 'callprep-service' OR login ~ '^[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}$');

COMMIT;

SELECT login, role, salesrep_id, enabled FROM callprep.user_access ORDER BY role, login;
