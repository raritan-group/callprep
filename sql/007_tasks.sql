-- 007_tasks.sql — tasks between reps, with P21 as the system of record.
--
-- Flow:  Call Prep (Hetzner) -> callprep.task_outbox  --(VMSQL2 Agent job, 5 min: callprep_task_writeback.ps1)-->  P21 activity_trans
--        P21 activity_trans  --(reports_push.ps1, 15 min)-->  public.activity_trans  -->  callprep.task (scoped view, unioned with pending outbox rows)
-- P21's own procedure p21_add_activity_trans allocates the number, so a task created here is a real P21 task (Task Manager, Outlook flag).
-- Identity: callprep.user_access.login (M365 email) <-> P21 users.email_address, via public.p21_users synced from P21.
-- Apply as postgres. Additive, re-runnable.

-- synced from P21 by reports_push.ps1 (full replace each run)
CREATE TABLE IF NOT EXISTS public.p21_users (
  id            text PRIMARY KEY,          -- P21 login id, e.g. JCOOK, DDICKMAN
  name          text,
  email_address text
);
CREATE TABLE IF NOT EXISTS public.activity_trans (
  activity_trans_no    text PRIMARY KEY,
  activity_id          text,               -- CALL, VISIT, QUOTE FU, CUST_FU, PRE-BID, MTG ...
  contact_id           text,
  entry_date           date,
  assigned_by_id       text,
  assigned_to_id       text,
  completed_flag       text,
  completed_date       timestamp,
  completed_by_id      text,
  subject              text,
  comments             text,
  target_complete_date timestamp,
  transaction_type_cd  int,                -- 300 none | 709 quote | 222 order | 2714 opportunity
  transaction_no       text,
  link_type_cd         int,                -- 1203 customer | 216 supplier | 1024 none
  link_id              text,               -- customer_id when link_type_cd = 1203
  date_created         timestamp,
  date_last_modified   timestamp
);
CREATE INDEX IF NOT EXISTS activity_trans_assigned_idx ON public.activity_trans (assigned_to_id, completed_flag);
CREATE INDEX IF NOT EXISTS activity_trans_link_idx ON public.activity_trans (link_id);
GRANT SELECT ON public.p21_users, public.activity_trans TO reportsapp;
GRANT INSERT, UPDATE, DELETE, TRUNCATE ON public.p21_users, public.activity_trans TO reportsapp;

-- what Call Prep writes; the VMSQL2 job applies it to P21 and records the result
CREATE TABLE IF NOT EXISTS callprep.task_outbox (
  id                bigserial PRIMARY KEY,
  created_at        timestamptz NOT NULL DEFAULT now(),
  created_by        text NOT NULL,                       -- login (email)
  op                text NOT NULL CHECK (op IN ('create','complete')),
  -- create
  activity_id       text,                                -- P21 activity type code
  customer_id       text,
  assigned_to       text,                                -- login (email) of the assignee
  subject           text,
  comments          text,
  due_date          date,
  transaction_type_cd int,                               -- 709 quote / 222 order / 2714 opportunity, NULL = none
  transaction_no    text,
  -- complete
  activity_trans_no text,                                -- target for 'complete'; filled in for 'create' once P21 has it
  -- result
  status            text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','processing','done','failed')),
  attempts          int NOT NULL DEFAULT 0,
  error             text,
  applied_at        timestamptz
);
CREATE INDEX IF NOT EXISTS task_outbox_status_idx ON callprep.task_outbox (status, id);

-- who is who: login <-> P21 user id, resolved by email (both lower-cased)
CREATE OR REPLACE VIEW callprep.p21_user AS
SELECT lower(u.email_address) AS login, u.id AS p21_user_id, u.name
FROM public.p21_users u WHERE u.email_address IS NOT NULL;

-- the P21 activity types worth offering from the page (P21 activity table is not synced; keep the short list here)
CREATE TABLE IF NOT EXISTS callprep.task_type (
  activity_id text PRIMARY KEY, label text NOT NULL, sort int NOT NULL
);
INSERT INTO callprep.task_type VALUES
  ('CUST_FU','Follow up with customer',1), ('QUOTE FU','Quote follow-up',2), ('CALL','Call',3), ('VISIT','Visit',4),
  ('MTG','Meeting',5), ('QUOTE','Quote',6), ('SUBMITTAL','Submittal',7), ('PRE-BID','Pre-bid',8), ('COMPLAINT','Complaint',9)
ON CONFLICT (activity_id) DO NOTHING;

-- Tasks as the signed-in person sees them: P21 tasks assigned to or by them (managers/admins: everyone), plus outbox rows
-- not yet in P21. Customer names come through the SCOPED customer view for reps; a task on an account outside a rep's
-- book still shows (it was assigned to them on purpose) but with the id only.
CREATE OR REPLACE VIEW callprep.task AS
WITH me AS (SELECT p21_user_id FROM callprep.p21_user WHERE login = callprep.current_login()),
p21 AS (
  SELECT a.activity_trans_no, a.activity_id, a.subject, a.comments,
         a.target_complete_date::date AS due_date, a.completed_flag = 'Y' AS completed, a.completed_date,
         a.assigned_by_id AS assigned_by_p21, a.assigned_to_id AS assigned_to_p21,
         CASE WHEN a.link_type_cd = 1203 THEN a.link_id END AS customer_id,
         a.transaction_type_cd, a.transaction_no, a.date_created, 'p21'::text AS source, NULL::bigint AS outbox_id, NULL::text AS outbox_status
  FROM public.activity_trans a
  WHERE callprep.sees_all() OR a.assigned_to_id IN (SELECT p21_user_id FROM me) OR a.assigned_by_id IN (SELECT p21_user_id FROM me)),
pending AS (
  SELECT NULL::text AS activity_trans_no, o.activity_id, o.subject, o.comments, o.due_date, false AS completed, NULL::timestamp AS completed_date,
         pb.p21_user_id AS assigned_by_p21, pa.p21_user_id AS assigned_to_p21, o.customer_id, o.transaction_type_cd, o.transaction_no,
         o.created_at::timestamp AS date_created, 'outbox'::text AS source, o.id AS outbox_id, o.status AS outbox_status
  FROM callprep.task_outbox o
  LEFT JOIN callprep.p21_user pb ON pb.login = o.created_by
  LEFT JOIN callprep.p21_user pa ON pa.login = o.assigned_to
  WHERE o.op = 'create' AND o.status IN ('pending','processing','failed')
    AND (callprep.sees_all() OR o.assigned_to = callprep.current_login() OR o.created_by = callprep.current_login()))
SELECT t.*, ub.name AS assigned_by_name, ua.name AS assigned_to_name, ua.login AS assigned_to_login,
       c.customer_name
FROM (SELECT * FROM p21 UNION ALL SELECT * FROM pending) t
LEFT JOIN callprep.p21_user ub ON ub.p21_user_id = t.assigned_by_p21
LEFT JOIN callprep.p21_user ua ON ua.p21_user_id = t.assigned_to_p21
LEFT JOIN callprep.customer_all c ON c.customer_id::text = t.customer_id AND (callprep.sees_all() OR c.customer_id IN (SELECT customer_id FROM callprep.scope_customer));

GRANT SELECT ON callprep.task, callprep.p21_user, callprep.task_type TO callprep_ro;
GRANT SELECT, INSERT ON callprep.task_outbox TO callprep_ro;
GRANT USAGE, SELECT ON SEQUENCE callprep.task_outbox_id_seq TO callprep_ro;
-- the VMSQL2 job connects as reportsapp and updates status/activity_trans_no
GRANT SELECT, UPDATE ON callprep.task_outbox TO reportsapp;
GRANT USAGE ON SCHEMA callprep TO reportsapp;
GRANT SELECT ON callprep.p21_user, callprep.task, callprep.task_type, callprep.user_access TO reportsapp;   -- the claim query resolves logins to P21 ids

-- a rep may only create tasks as themselves and only complete tasks they can see
CREATE OR REPLACE FUNCTION callprep.task_outbox_guard() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER AS $$
BEGIN
  IF session_user = 'callprep_ro' THEN            -- SECURITY DEFINER: current_user here is the owner (postgres); session_user is the app role
    IF NEW.created_by IS DISTINCT FROM callprep.current_login() THEN
      RAISE EXCEPTION 'task_outbox.created_by must be the session login';
    END IF;
    IF NEW.op = 'create' AND NEW.assigned_to NOT IN (SELECT login FROM callprep.user_access WHERE enabled) THEN
      RAISE EXCEPTION 'assignee % is not a Call Prep user', NEW.assigned_to;
    END IF;
    IF NEW.op = 'create' AND NEW.assigned_to NOT IN (SELECT login FROM callprep.p21_user) THEN
      RAISE EXCEPTION 'assignee % has no P21 user with that email', NEW.assigned_to;
    END IF;
    IF NEW.op = 'complete' AND NOT EXISTS (SELECT 1 FROM callprep.task t WHERE t.activity_trans_no = NEW.activity_trans_no) THEN
      RAISE EXCEPTION 'task % is not visible to you', NEW.activity_trans_no;
    END IF;
  END IF;
  RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS task_outbox_guard ON callprep.task_outbox;
CREATE TRIGGER task_outbox_guard BEFORE INSERT ON callprep.task_outbox FOR EACH ROW EXECUTE FUNCTION callprep.task_outbox_guard();
