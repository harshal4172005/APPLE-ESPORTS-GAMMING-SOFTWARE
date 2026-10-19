import api from '../config/api';

const generateIdempotencyKey = () =>
  Math.random().toString(36).substring(2) + Date.now().toString(36);

export const getMembers = (branchId, search, page = 1, pageSize = 50, includeDeleted = false) => {
  const params = { page, pageSize };
  if (search) params.search = search;
  if (includeDeleted) params.includeDeleted = true;
  // BranchIsolation filter: SuperAdmin needs branchId query param; operators get it from JWT
  if (branchId) params.branchId = branchId;
  return api.get('/members', { params }).then(r => r.data?.data);
};

export const getMemberById = (id) =>
  api.get(`/members/${id}`).then(r => r.data?.data);

export const getMemberByPhone = (phone) =>
  api.get(`/members/phone/${phone}`).then(r => r.data?.data);

export const registerMember = (dto) =>
  api.post('/members', dto).then(r => r.data?.data);

export const updateMember = (id, dto) =>
  api.put(`/members/${id}`, dto).then(r => r.data?.data);

export const adminEditMemberValues = (id, dto) =>
  api.put(`/members/${id}/admin-edit`, dto).then(r => r.data?.data);

export const getWalletHistory = (memberId, page = 1, pageSize = 30) =>
  api.get(`/wallets/${memberId}`, { params: { page, pageSize } }).then(r => r.data?.data);

// Combined gaming-session + wallet top-up/deduction history for one member, across every
// branch they've played at. fromDate/toDate (yyyy-MM-dd) are optional - omit for all-time.
export const getMemberHistory = (memberId, fromDate, toDate) =>
  api.get(`/members/${memberId}/history`, { params: { fromDate, toDate } }).then(r => r.data?.data);

export const topUpWallet = (memberId, dto) =>
  api.post(`/wallets/${memberId}/topup`, dto, {
    headers: { 'X-Idempotency-Key': generateIdempotencyKey() },
  }).then(r => r.data?.data);

// Free bonus - never counted as money received. Returns { queued: true, ... } when sent from
// Head Office to the member's branch, or the wallet transaction when applied at a branch.
export const giveMemberBonus = (memberId, dto) =>
  api.post(`/members/${memberId}/bonus`, dto, {
    headers: { 'X-Idempotency-Key': generateIdempotencyKey() },
  }).then(r => r.data?.data);

export const deductWallet = (memberId, dto) =>
  api.post(`/wallets/${memberId}/deduct`, dto, {
    headers: { 'X-Idempotency-Key': generateIdempotencyKey() },
  }).then(r => r.data?.data);

export const deleteMember = (id) =>
  api.delete(`/members/${id}`).then(r => r.data);

export const memberLogin = (dto) =>
  api.post('/members/login', dto).then(r => r.data?.data);
