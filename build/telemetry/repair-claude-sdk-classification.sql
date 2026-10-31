-- Run with psql -X -qAt. Required variables: organization_id, from_utc, to_utc.
-- Default: preview only. Explicit -v apply=true changes classification, never billing.
\set ON_ERROR_STOP on
\if :{?organization_id}
\else
  DO $$ BEGIN RAISE EXCEPTION 'Required: -v organization_id=...'; END $$;
\endif
\if :{?from_utc}
\else
  DO $$ BEGIN RAISE EXCEPTION 'Required: -v from_utc=...'; END $$;
\endif
\if :{?to_utc}
\else
  DO $$ BEGIN RAISE EXCEPTION 'Required: -v to_utc=...'; END $$;
\endif
\if :{?apply}
\else
  \set apply false
\endif

BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '60s';
SET LOCAL TIME ZONE 'UTC';

SELECT length(:'organization_id') BETWEEN 1 AND 128
    AND :'to_utc'::timestamptz > :'from_utc'::timestamptz
    AND :'to_utc'::timestamptz - :'from_utc'::timestamptz <= interval '30 days'
    AND :'apply' IN ('true', 'false') AS valid_input \gset
\if :valid_input
\else
  ROLLBACK;
  DO $$ BEGIN
    RAISE EXCEPTION 'Invalid organization, time range (maximum 30 days), or apply flag.';
  END $$;
\endif

-- Cap the preview scan at one sentinel past the limit. An oversized window aborts,
-- rather than applying a truncated repair that could look complete to the operator.
CREATE TEMP TABLE claude_sdk_repair_candidates ON COMMIT DROP AS
SELECT event.organization_id, event.id, event.occurred_at_utc,
       event.conversation_id, event.event_type, event.query_source,
       event.input_tokens, event.output_tokens, event.cost_usd
FROM zeeq.agent_session_events AS event
JOIN zeeq.agent_conversations AS conversation
  ON conversation.organization_id = event.organization_id
 AND conversation.id = event.conversation_id
WHERE event.organization_id = :'organization_id'
  AND event.occurred_at_utc >= :'from_utc'::timestamptz
  AND event.occurred_at_utc < :'to_utc'::timestamptz
  AND conversation.harness = 'claude-code'
  AND event.query_source = 'sdk'
  AND event.is_housekeeping = true
ORDER BY event.occurred_at_utc, event.id
LIMIT 10001;

SELECT count(*) <= 10000 AS within_limit FROM claude_sdk_repair_candidates \gset
\if :within_limit
\else
  ROLLBACK;
  DO $$ BEGIN
    RAISE EXCEPTION 'More than 10000 candidates. Narrow the time range; no changes applied.';
  END $$;
\endif

\if :apply
  CREATE TEMP TABLE claude_sdk_repair_changed ON COMMIT DROP AS
  SELECT * FROM claude_sdk_repair_candidates WITH NO DATA;

  -- Recheck mutable eligibility under the update lock. Concurrent reruns cannot
  -- both change the same old flag. Composite identity includes the partition key.
  WITH changed AS (
    UPDATE zeeq.agent_session_events AS event
       SET is_housekeeping = false
      FROM claude_sdk_repair_candidates AS candidate,
           zeeq.agent_conversations AS conversation
     WHERE event.organization_id = candidate.organization_id
       AND event.organization_id = :'organization_id'
       AND event.occurred_at_utc >= :'from_utc'::timestamptz
       AND event.occurred_at_utc < :'to_utc'::timestamptz
       AND event.id = candidate.id
       AND event.occurred_at_utc = candidate.occurred_at_utc
       AND conversation.organization_id = event.organization_id
       AND conversation.id = event.conversation_id
       AND conversation.harness = 'claude-code'
       AND event.query_source = 'sdk'
       AND event.is_housekeeping = true
    RETURNING event.organization_id, event.id, event.occurred_at_utc,
              event.conversation_id, event.event_type, event.query_source,
              event.input_tokens, event.output_tokens, event.cost_usd
  )
  INSERT INTO claude_sdk_repair_changed SELECT * FROM changed;

  SELECT jsonb_build_object('action', 'reclassified_pending_commit', 'old_is_housekeeping', true,
                           'new_is_housekeeping', false) || to_jsonb(changed)
  FROM claude_sdk_repair_changed AS changed
  ORDER BY occurred_at_utc, id;
  SELECT jsonb_build_object('changed_count', count(*),
                           'retained_input_tokens', coalesce(sum(input_tokens), 0),
                           'retained_output_tokens', coalesce(sum(output_tokens), 0),
                           'retained_known_cost_usd', coalesce(sum(cost_usd), 0))
  FROM claude_sdk_repair_changed;
  SELECT count(*) AS changed_count FROM claude_sdk_repair_changed \gset
  COMMIT;
  SELECT jsonb_build_object('committed', true, 'changed_count', :changed_count);
\else
  SELECT jsonb_build_object('action', 'candidate', 'old_is_housekeeping', true)
         || to_jsonb(candidate)
  FROM claude_sdk_repair_candidates AS candidate
  ORDER BY occurred_at_utc, id;
  SELECT jsonb_build_object('candidate_count', count(*), 'applied', false)
  FROM claude_sdk_repair_candidates;
  ROLLBACK;
\endif
