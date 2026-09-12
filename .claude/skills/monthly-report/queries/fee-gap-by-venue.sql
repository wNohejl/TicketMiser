-- Per venue in :month, the median gap between all-in and face-value lowest price on the
-- primary market, as a percentage of face. Only rows where both numbers were recorded.
-- Usage: psql -v month='2026-09' -f fee-gap-by-venue.sql

select v.name as venue,
       count(*) as observations,
       round(100 * percentile_cont(0.5) within group (order by (p.lowest - p.face_min) / p.face_min)::numeric, 1) as median_fee_pct,
       round(100 * min((p.lowest - p.face_min) / p.face_min)::numeric, 1) as min_fee_pct,
       round(100 * max((p.lowest - p.face_min) / p.face_min)::numeric, 1) as max_fee_pct
from price_observations p
join sources s on s.id = p.source_id and s.kind = 'Primary'
join events e on e.id = p.event_id
join venues v on v.id = e.venue_id
where p.all_in = true
  and p.face_min is not null and p.face_min > 0
  and to_char(p.observed_at, 'YYYY-MM') = :'month'
group by v.name
order by median_fee_pct desc;
